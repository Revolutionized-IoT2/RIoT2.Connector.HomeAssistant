using RIoT2.Connector.Sdk;
using RIoT2.Core.Configuration;

namespace RIoT2.Connector.HomeAssistant.Services
{
    /// <summary>
    /// The connector's own settings (<c>RIOT2_HA_*</c>); the shared connector settings (id, name, URL, the RIoT2 broker,
    /// logging, telemetry, security) are the connector SDK's <see cref="ConnectorOptions"/>.
    /// </summary>
    public sealed class HomeAssistantOptions
    {
        public const string Component = "Home Assistant connector";
        public const string DiscoveryPrefixVariable = "RIOT2_HA_DISCOVERY_PREFIX";
        public const string TopicPrefixVariable = "RIOT2_HA_TOPIC_PREFIX";
        public const string CommandTtlVariable = "RIOT2_HA_COMMAND_TTL_SECONDS";
        public const string MqttHostVariable = "RIOT2_HA_MQTT_IP";
        public const string MqttPortVariable = "RIOT2_HA_MQTT_PORT";
        public const string MqttUsernameVariable = "RIOT2_HA_MQTT_USERNAME";
        public const string MqttPasswordVariable = "RIOT2_HA_MQTT_PASSWORD";
        public const string MqttTlsVariable = "RIOT2_HA_MQTT_TLS";
        public const string MqttCaFileVariable = "RIOT2_HA_MQTT_CA_FILE";

        /// <summary>Home Assistant's MQTT discovery prefix; <c>homeassistant</c> by default.</summary>
        public string DiscoveryPrefix { get; set; } = "homeassistant";

        /// <summary>Where the connector publishes states and availability and receives commands; <c>riot2/ha</c> by default.</summary>
        public string TopicPrefix { get; set; } = "riot2/ha";

        /// <summary>The time-to-live of a command from Home Assistant; 60 s by default.</summary>
        public TimeSpan CommandTimeToLive { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The broker Home Assistant uses, when it isn't the RIoT2 broker (<c>RIOT2_HA_MQTT_IP</c> set); <c>null</c> uses
        /// the RIoT2 broker.
        /// </summary>
        public MqttOptions? Mqtt { get; set; }

        public static HomeAssistantOptions Read(SettingsReader settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var options = new HomeAssistantOptions
            {
                DiscoveryPrefix = Prefix(settings, DiscoveryPrefixVariable, "homeassistant"),
                TopicPrefix = Prefix(settings, TopicPrefixVariable, "riot2/ha"),
                CommandTimeToLive = TimeSpan.FromSeconds(settings.Integer(CommandTtlVariable, 60, 1, 86400))
            };
            var host = settings.Optional(MqttHostVariable)?.Trim();
            if (!string.IsNullOrEmpty(host))
            {
                var useTls = settings.Flag(MqttTlsVariable);
                var caFile = settings.Optional(MqttCaFileVariable)?.Trim();
                options.Mqtt = new MqttOptions
                {
                    Host = host,
                    Port = settings.Integer(MqttPortVariable, useTls ? 8883 : 1883, 1, 65535),
                    Username = settings.Optional(MqttUsernameVariable),
                    Password = settings.Optional(MqttPasswordVariable),
                    UseTls = useTls,
                    CaFile = string.IsNullOrEmpty(caFile) ? null : caFile
                };
                if (options.Mqtt.CaFile != null && !useTls)
                    settings.AddError($"{MqttCaFileVariable} needs {MqttTlsVariable}=true");
                else if (options.Mqtt.CaFile != null && !File.Exists(options.Mqtt.CaFile))
                    settings.AddError($"{MqttCaFileVariable} names a file that doesn't exist: {options.Mqtt.CaFile}");
            }
            return options;
        }

        // A topic prefix: no wildcards, no leading or trailing slash.
        private static string Prefix(SettingsReader settings, string variable, string defaultValue)
        {
            var value = (settings.Optional(variable) ?? defaultValue).Trim().Trim('/');
            if (value.Length == 0 || value.IndexOfAny(['+', '#']) >= 0)
            {
                settings.AddError($"{variable} must be an MQTT topic without wildcards: '{value}'");
                return defaultValue;
            }
            return value;
        }

        /// <summary>
        /// The SDK settings this connector fixes: the name <c>Home Assistant</c> (unless <c>RIOT2_CONNECTOR_NAME</c> is
        /// set), no disk spool (Home Assistant shows the latest state; a backlog has no use) and no batch delay.
        /// </summary>
        public static void Configure(ConnectorOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Name = "Home Assistant";
            options.Spool.Enabled = false;
            options.BatchInterval = TimeSpan.Zero;
        }

        /// <summary>Reads every setting as the start does, and throws <see cref="ConfigurationValidationException"/> listing every problem.</summary>
        public static (ConnectorOptions Connector, HomeAssistantOptions HomeAssistant) Load(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
            ConnectorOptions.Load(configuration, Component, Read);
    }
}
