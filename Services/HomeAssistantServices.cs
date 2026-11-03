using RIoT2.Connector.Sdk;
using RIoT2.Core.Configuration;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;

namespace RIoT2.Connector.HomeAssistant.Services
{
    /// <summary>What the other services need of the Home Assistant MQTT connection (a fake in the tests).</summary>
    public interface IHomeAssistantMqtt
    {
        bool IsConnected { get; }
        Task PublishAsync(string topic, string payload, bool retain);
    }

    /// <summary>
    /// The MQTT connection to Home Assistant's broker: the RIoT2 broker unless <c>RIOT2_HA_MQTT_IP</c> names another.
    /// Its last will marks every entity unavailable (<c>{prefix}/status</c> = <c>offline</c>, retained); after each
    /// connection it publishes <c>online</c> and calls <see cref="Connected"/>.
    /// </summary>
    public sealed class HomeAssistantConnection : IHomeAssistantMqtt, IAsyncDisposable
    {
        private readonly MqttClient _client;
        private readonly string _availabilityTopic;

        public HomeAssistantConnection(ConnectorOptions connector, HomeAssistantOptions options, ILogger<HomeAssistantConnection> logger)
        {
            ArgumentNullException.ThrowIfNull(connector);
            ArgumentNullException.ThrowIfNull(options);
            var mqtt = options.Mqtt ?? connector.Mqtt;
            _client = new MqttClient(mqtt.ToConfiguration(connector.Id + "-ha"), logger);
            _availabilityTopic = HomeAssistantEntities.AvailabilityTopic(options);
            _client.SetLastWill(_availabilityTopic, "offline", retain: true);
            _client.ConnectedAsync += OnConnectedAsync;
            _client.MessageReceivedAsync += message => MessageReceived?.Invoke(message) ?? Task.CompletedTask;
        }

        /// <summary>Called after every (re)connection, once <c>online</c> is published.</summary>
        public Func<Task>? Connected { get; set; }

        /// <summary>Called for every message on the subscribed topics.</summary>
        public Func<MqttEventArgs, Task>? MessageReceived { get; set; }

        public bool IsConnected => _client.IsConnected();

        public Task StartAsync(params string[] topics) => _client.Start(topics);

        /// <summary>Publishes <c>offline</c> (the last will) and disconnects.</summary>
        public Task StopAsync(CancellationToken cancellationToken) => _client.StopAsync(cancellationToken);

        public Task PublishAsync(string topic, string payload, bool retain) => _client.PublishAsync(topic, payload, MqttQos.AtLeastOnce, retain);

        public ValueTask DisposeAsync() => _client.DisposeAsync();

        private async Task OnConnectedAsync()
        {
            await _client.PublishAsync(_availabilityTopic, "online", MqttQos.AtLeastOnce, retain: true).ConfigureAwait(false);
            if (Connected != null)
                await Connected().ConfigureAwait(false);
        }
    }

    /// <summary>The entities published last, by command topic, for the command bridge.</summary>
    public sealed class HomeAssistantEntityMap
    {
        private volatile IReadOnlyDictionary<string, HomeAssistantEntity> _byCommandTopic = new Dictionary<string, HomeAssistantEntity>();
        private volatile IReadOnlyList<HomeAssistantEntity> _all = [];

        public IReadOnlyList<HomeAssistantEntity> All => _all;

        public void Set(IReadOnlyList<HomeAssistantEntity> entities)
        {
            _byCommandTopic = entities.Where(e => e.CommandTopic != null).ToDictionary(e => e.CommandTopic!, StringComparer.Ordinal);
            _all = entities;
        }

        public HomeAssistantEntity? FindCommand(string topic) => _byCommandTopic.TryGetValue(topic, out var entity) ? entity : null;
    }

    /// <summary>
    /// Publishes the discovery messages (retained) whenever the templates are loaded, Home Assistant restarts or the
    /// connection comes back, and deletes this connector's discovery messages whose template is gone.
    /// </summary>
    public sealed class DiscoveryPublisher : IConnectorTemplateObserver
    {
        private readonly IHomeAssistantMqtt _mqtt;
        private readonly HomeAssistantEntityMap _map;
        private readonly HomeAssistantOptions _options;
        private readonly string _connectorId;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _seenLock = new();
        // Discovery topics of this connector seen on the broker (retained from earlier runs, or just published).
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private TemplateSnapshot? _templates;

        public static readonly string? Version = typeof(DiscoveryPublisher).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0];

        public DiscoveryPublisher(IHomeAssistantMqtt mqtt, HomeAssistantEntityMap map, HomeAssistantOptions options, ConnectorOptions connector,
            ILogger<DiscoveryPublisher> logger)
        {
            _mqtt = mqtt;
            _map = map;
            _options = options;
            _connectorId = connector.Id;
            _logger = logger;
        }

        /// <summary>Records a discovery message of this connector seen on the broker; an empty one is a deletion.</summary>
        public void Seen(string topic, string payload)
        {
            lock (_seenLock)
            {
                if (string.IsNullOrEmpty(payload))
                    _seen.Remove(topic);
                else
                    _seen.Add(topic);
            }
        }

        public Task TemplatesLoadedAsync(TemplateSnapshot templates, CancellationToken cancellationToken)
        {
            _templates = templates;
            return PublishAsync(cancellationToken);
        }

        /// <summary>Publishes every entity again, and deletes the stale ones; nothing before the templates are loaded.</summary>
        public async Task PublishAsync(CancellationToken cancellationToken)
        {
            var templates = _templates;
            if (templates == null || !_mqtt.IsConnected)
                return;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entities = HomeAssistantEntities.Build(templates, _options, _connectorId, Version);
                _map.Set(entities);
                foreach (var entity in entities)
                    await _mqtt.PublishAsync(entity.ConfigTopic, entity.Config.ToJsonString(), retain: true).ConfigureAwait(false);

                var current = entities.Select(e => e.ConfigTopic).ToHashSet(StringComparer.Ordinal);
                string[] stale;
                lock (_seenLock)
                {
                    stale = _seen.Where(t => !current.Contains(t)).ToArray();
                    _seen.ExceptWith(stale);
                    _seen.UnionWith(current);
                }
                foreach (var topic in stale)
                    await _mqtt.PublishAsync(topic, "", retain: true).ConfigureAwait(false);
                _logger.LogInformation("Published {Count} Home Assistant entities, removed {Stale}", entities.Count, stale.Length);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Publishes every report's (and variable's) value as the state of its entity, retained, so Home Assistant has it
    /// after a restart. Until the templates are loaded or while Home Assistant's broker is away, a batch is transient.
    /// </summary>
    public sealed class HomeAssistantSink : IConnectorSink
    {
        private readonly IHomeAssistantMqtt _mqtt;
        private readonly HomeAssistantOptions _options;
        private readonly ILogger<HomeAssistantSink> _logger;

        public HomeAssistantSink(IHomeAssistantMqtt mqtt, HomeAssistantOptions options, ILogger<HomeAssistantSink> logger)
        {
            _mqtt = mqtt;
            _options = options;
            _logger = logger;
        }

        public async Task WriteAsync(IReadOnlyList<ConnectorMessage> batch, ITemplateCatalog templates, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(templates);
            var snapshot = templates.Snapshot
                ?? throw new TransientSinkException("The templates are not loaded yet; waiting for the orchestrator's configuration command.");
            if (!_mqtt.IsConnected)
                throw new TransientSinkException("Not connected to Home Assistant's MQTT broker.");
            foreach (var message in batch)
            {
                var state = State(message, snapshot, _options);
                if (state == null)
                {
                    ConnectorMetrics.RecordIgnored(message);
                    continue;
                }
                try
                {
                    await _mqtt.PublishAsync(state.Value.Topic, state.Value.Payload, retain: true).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new TransientSinkException("Could not publish to Home Assistant's MQTT broker.", error);
                }
            }
        }

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(_mqtt.IsConnected);

        /// <summary>The state topic and payload of a report; <c>null</c> for a command, an unknown report or no value.</summary>
        public static (string Topic, string Payload)? State(ConnectorMessage message, TemplateSnapshot templates, HomeAssistantOptions options)
        {
            if (message.Kind != ConnectorMessageKind.Report)
                return null;
            var report = Report.Create(message.Json);
            if (report?.Value == null || templates.FindReport(report.Id) == null)
                return null;
            return ($"{options.TopicPrefix}/{HomeAssistantEntities.Sanitize(report.Id)}/state", HomeAssistantEntities.StatePayload(report.Value));
        }
    }

    /// <summary>
    /// Turns a command from Home Assistant into a RIoT2 command through <see cref="IRiot2CommandClient"/>
    /// (<c>POST /api/v2/commands</c>), so it goes through the orchestrator's outbox like every other command.
    /// </summary>
    public sealed class CommandBridge
    {
        private readonly HomeAssistantEntityMap _map;
        private readonly IRiot2CommandClient _commands;
        private readonly HomeAssistantOptions _options;
        private readonly ILogger<CommandBridge> _logger;

        public CommandBridge(HomeAssistantEntityMap map, IRiot2CommandClient commands, HomeAssistantOptions options, ILogger<CommandBridge> logger)
        {
            _map = map;
            _commands = commands;
            _options = options;
            _logger = logger;
        }

        /// <summary>Sends the command of <paramref name="topic"/>; returns its delivery, or <c>null</c> when nothing was sent.</summary>
        public async Task<CommandDeliveryInfo?> HandleAsync(string topic, string payload, CancellationToken cancellationToken)
        {
            var entity = _map.FindCommand(topic);
            if (entity == null)
            {
                _logger.LogWarning("Ignored a command on {Topic}: no RIoT2 command has that topic", topic);
                return null;
            }
            var value = HomeAssistantEntities.CommandValue(payload, entity.Type);
            if (value == null)
            {
                _logger.LogWarning("Ignored a command for {Command}: '{Payload}' isn't a {Type}", entity.TemplateId, payload, entity.Type);
                return null;
            }
            try
            {
                var delivery = await _commands.SendAsync(entity.TemplateId, value, new CommandSendOptions { TimeToLive = _options.CommandTimeToLive }, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Command {Command} from Home Assistant queued as {CorrelationId}", entity.TemplateId, delivery.CorrelationId);
                return delivery;
            }
            catch (Exception error) when (error is CommandRejectedException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(error, "Could not send command {Command} from Home Assistant", entity.TemplateId);
                return null;
            }
        }
    }

    /// <summary>
    /// Starts the Home Assistant connection and routes its messages: Home Assistant's birth message and every
    /// reconnection republish the discovery messages, <c>{prefix}/+/set</c> goes to the <see cref="CommandBridge"/>, and
    /// this connector's discovery topics are recorded for the stale-entity cleanup.
    /// </summary>
    public sealed class HomeAssistantBridge : IHostedService
    {
        private readonly HomeAssistantConnection _connection;
        private readonly DiscoveryPublisher _discovery;
        private readonly CommandBridge _commands;
        private readonly HomeAssistantOptions _options;
        private readonly ILogger<HomeAssistantBridge> _logger;
        private readonly string _birthTopic;
        private readonly string _commandFilter;
        private readonly string _configFilter;
        private readonly CancellationTokenSource _stopping = new();

        public HomeAssistantBridge(HomeAssistantConnection connection, DiscoveryPublisher discovery, CommandBridge commands,
            HomeAssistantOptions options, ConnectorOptions connector, ILogger<HomeAssistantBridge> logger)
        {
            _connection = connection;
            _discovery = discovery;
            _commands = commands;
            _options = options;
            _logger = logger;
            _birthTopic = options.DiscoveryPrefix + "/status";
            _commandFilter = HomeAssistantEntities.CommandFilter(options);
            _configFilter = HomeAssistantEntities.ConfigFilter(options, connector.Id);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _connection.MessageReceived = HandleAsync;
            _connection.Connected = () => _discovery.PublishAsync(_stopping.Token);
            try
            {
                await _connection.StartAsync(_birthTopic, _commandFilter, _configFilter).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Could not connect to Home Assistant's MQTT broker");
                throw new InvalidOperationException("Could not connect to Home Assistant's MQTT broker.", error);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            await _connection.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        // Internal for tests.
        internal async Task HandleAsync(MqttEventArgs message)
        {
            try
            {
                if (message.Topic == _birthTopic)
                {
                    if (string.Equals(message.Message.Trim(), "online", StringComparison.OrdinalIgnoreCase))
                        await _discovery.PublishAsync(_stopping.Token).ConfigureAwait(false);
                }
                else if (MqttClient.IsMatch(message.Topic, _configFilter))
                    _discovery.Seen(message.Topic, message.Message);
                else if (MqttClient.IsMatch(message.Topic, _commandFilter))
                    await _commands.HandleAsync(message.Topic, message.Message, _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogError(error, "Could not process the Home Assistant message on {Topic}", message.Topic);
            }
        }
    }
}
