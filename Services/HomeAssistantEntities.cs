using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using RIoT2.Connector.Sdk;
using RIoT2.Core.Models;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Connector.HomeAssistant.Services
{
    /// <summary>One Home Assistant entity of a RIoT2 report, variable or command template, with its discovery message.</summary>
    public sealed record HomeAssistantEntity(
        string TemplateId,
        bool IsCommand,
        ValueType Type,
        string Component,
        string ObjectId,
        string ConfigTopic,
        JsonObject Config,
        string? StateTopic,
        string? CommandTopic);

    /// <summary>
    /// Maps the RIoT2 templates to Home Assistant MQTT discovery (design 7.3 phase 4):
    /// <list type="bullet">
    /// <item>a report or variable becomes a <c>binary_sensor</c> (boolean) or a <c>sensor</c> (number with the template's
    /// <c>unit</c> and <c>precision</c>, text, entity with its fields as attributes, text array as JSON);</item>
    /// <item>a command becomes a <c>switch</c> (boolean; its state is the report of the same device and address, else
    /// optimistic), a <c>number</c> or a <c>text</c> (text, entity and text array as JSON).</item>
    /// </list>
    /// Entities are grouped by RIoT2 device; variables form the device "RIoT2 variables". Every entity uses the
    /// connector's availability topic.
    /// </summary>
    public static class HomeAssistantEntities
    {
        /// <summary>The longest text Home Assistant keeps as a state.</summary>
        public const int MaxStateLength = 255;

        public static IReadOnlyList<HomeAssistantEntity> Build(TemplateSnapshot templates, HomeAssistantOptions options, string connectorId, string? version)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(options);
            var nodeId = "riot2_" + Sanitize(connectorId);
            var entities = new List<HomeAssistantEntity>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var reportStates = new Dictionary<(string Device, string Address), string>();
            foreach (var (template, isVariable) in templates.Reports.Select(r => (r, false)).Concat(templates.Variables.Select(v => (v, true))))
            {
                var objectId = Sanitize(template.Id);
                if (!seen.Add(objectId))
                    continue;
                var entity = Report(template, isVariable, objectId, nodeId, options, version);
                entities.Add(entity);
                if (!isVariable && template.Type == ValueType.Boolean && !string.IsNullOrEmpty(template.DeviceId) && !string.IsNullOrEmpty(template.Address))
                    reportStates.TryAdd((template.DeviceId, template.Address!), entity.StateTopic!);
            }
            foreach (var template in templates.Commands)
            {
                var objectId = Sanitize(template.Id);
                if (!seen.Add(objectId))
                    continue;
                reportStates.TryGetValue((template.DeviceId, template.Address ?? ""), out var stateTopic);
                entities.Add(Command(template, objectId, nodeId, options, version, stateTopic));
            }
            return entities;
        }

        /// <summary>The discovery node id of a connector: <c>riot2_</c> and its id, so two connectors don't clash.</summary>
        public static string NodeId(string connectorId) => "riot2_" + Sanitize(connectorId);

        public static string AvailabilityTopic(HomeAssistantOptions options) => options.TopicPrefix + "/status";

        /// <summary>The topic filter for the commands from Home Assistant.</summary>
        public static string CommandFilter(HomeAssistantOptions options) => options.TopicPrefix + "/+/set";

        /// <summary>The topic filter for this connector's discovery messages, to find stale ones.</summary>
        public static string ConfigFilter(HomeAssistantOptions options, string connectorId) =>
            $"{options.DiscoveryPrefix}/+/{NodeId(connectorId)}/+/config";

        /// <summary>Letters, digits, <c>_</c> and <c>-</c>; anything else becomes <c>_</c>.</summary>
        public static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
                builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return builder.Length == 0 ? "_" : builder.ToString();
        }

        private static HomeAssistantEntity Report(ConnectorTemplate template, bool isVariable, string objectId, string nodeId, HomeAssistantOptions options, string? version)
        {
            var stateTopic = $"{options.TopicPrefix}/{objectId}/state";
            var component = template.Type == ValueType.Boolean ? "binary_sensor" : "sensor";
            var config = Common(template, isVariable, objectId, options, version);
            config["state_topic"] = stateTopic;
            switch (template.Type)
            {
                case ValueType.Boolean:
                    config["payload_on"] = "true";
                    config["payload_off"] = "false";
                    break;
                case ValueType.Number:
                    config["state_class"] = "measurement";
                    if (Parameter(template, "unit") is { } unit)
                        config["unit_of_measurement"] = unit;
                    if (int.TryParse(Parameter(template, "precision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var precision) && precision is >= 0 and <= 10)
                        config["suggested_display_precision"] = precision;
                    break;
                case ValueType.Entity:
                    // The fields are the attributes; the state is the time of the last value.
                    config["json_attributes_topic"] = stateTopic;
                    config["value_template"] = "{{ now().isoformat() }}";
                    config["device_class"] = "timestamp";
                    break;
            }
            return new HomeAssistantEntity(template.Id, false, template.Type, component, objectId, ConfigTopic(options, component, nodeId, objectId), config, stateTopic, null);
        }

        private static HomeAssistantEntity Command(ConnectorTemplate template, string objectId, string nodeId, HomeAssistantOptions options, string? version, string? reportState)
        {
            var commandTopic = $"{options.TopicPrefix}/{objectId}/set";
            var config = Common(template, false, objectId, options, version);
            config["command_topic"] = commandTopic;
            string component;
            switch (template.Type)
            {
                case ValueType.Boolean:
                    component = "switch";
                    config["payload_on"] = "true";
                    config["payload_off"] = "false";
                    if (reportState != null)
                    {
                        config["state_topic"] = reportState;
                        config["state_on"] = "true";
                        config["state_off"] = "false";
                    }
                    else
                        config["optimistic"] = true;
                    break;
                case ValueType.Number:
                    component = "number";
                    config["min"] = -1_000_000;
                    config["max"] = 1_000_000;
                    config["step"] = 0.001;
                    config["mode"] = "box";
                    config["optimistic"] = true;
                    break;
                default:
                    component = "text";
                    config["max"] = MaxStateLength;
                    config["optimistic"] = true;
                    break;
            }
            return new HomeAssistantEntity(template.Id, true, template.Type, component, objectId, ConfigTopic(options, component, nodeId, objectId), config, reportState, commandTopic);
        }

        private static JsonObject Common(ConnectorTemplate template, bool isVariable, string objectId, HomeAssistantOptions options, string? version)
        {
            var deviceKey = isVariable ? "variables" : Sanitize(string.IsNullOrEmpty(template.DeviceId) ? template.Node + "_" + template.Device : template.DeviceId);
            var device = new JsonObject
            {
                ["identifiers"] = new JsonArray("riot2_" + deviceKey),
                ["name"] = isVariable ? "RIoT2 variables" : string.IsNullOrEmpty(template.Device) ? "RIoT2 device" : template.Device,
                ["manufacturer"] = "RIoT2",
                ["model"] = isVariable ? "Variables" : "RIoT2 device"
            };
            var config = new JsonObject
            {
                ["name"] = template.Name,
                ["unique_id"] = "riot2_" + objectId,
                ["object_id"] = "riot2_" + objectId,
                ["availability_topic"] = AvailabilityTopic(options),
                ["device"] = device,
                ["origin"] = new JsonObject
                {
                    ["name"] = "RIoT2 Home Assistant connector",
                    ["sw_version"] = version ?? "",
                    ["support_url"] = "https://github.com/Revolutionized-IoT2/RIoT2.Connector.HomeAssistant"
                }
            };
            return config;
        }

        private static string ConfigTopic(HomeAssistantOptions options, string component, string nodeId, string objectId) =>
            $"{options.DiscoveryPrefix}/{component}/{nodeId}/{objectId}/config";

        private static string? Parameter(ConnectorTemplate template, string name)
        {
            if (template.Parameters == null)
                return null;
            foreach (var (key, value) in template.Parameters)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            return null;
        }

        /// <summary>
        /// The state a report's value is published as: <c>true</c>/<c>false</c>, an invariant number, the text (cut to
        /// <see cref="MaxStateLength"/>), or the JSON of an entity or array.
        /// </summary>
        public static string StatePayload(ValueModel value)
        {
            ArgumentNullException.ThrowIfNull(value);
            switch (value.Type)
            {
                case ValueType.Boolean:
                    return value.GetValue<bool>() ? "true" : "false";
                case ValueType.Number:
                    return value.ToJson();
                case ValueType.Text:
                    var text = value.GetValue<string>() ?? "";
                    return text.Length > MaxStateLength ? text[..MaxStateLength] : text;
                default:
                    return value.ToJson();
            }
        }

        /// <summary>
        /// The command value of a payload from Home Assistant, or <c>null</c> when it doesn't fit the command's type:
        /// <c>true</c>/<c>on</c>/<c>1</c> and <c>false</c>/<c>off</c>/<c>0</c> for a boolean, an invariant number, the text,
        /// or JSON (an entity must be an object).
        /// </summary>
        public static ValueModel? CommandValue(string payload, ValueType type)
        {
            payload ??= "";
            switch (type)
            {
                case ValueType.Boolean:
                    return payload.Trim().ToLowerInvariant() switch
                    {
                        "true" or "on" or "1" => new ValueModel(true),
                        "false" or "off" or "0" => new ValueModel(false),
                        _ => null
                    };
                case ValueType.Number:
                    return double.TryParse(payload.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                        ? new ValueModel(number)
                        : null;
                case ValueType.Text:
                    return new ValueModel((object)payload);
                case ValueType.Entity:
                    try
                    {
                        return JsonNode.Parse(payload) is JsonObject ? new ValueModel(payload, enforceCamelCase: false) : null;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        return null;
                    }
                default:
                    return new ValueModel(payload, enforceCamelCase: false);
            }
        }
    }
}
