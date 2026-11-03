# RIoT2.Connector.HomeAssistant

Shows RIoT2 devices in [Home Assistant](https://www.home-assistant.io/) through
[MQTT discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery), in both directions: reports
become sensors, and switches, numbers and texts send RIoT2 commands. Built on the RIoT2
[connector SDK](https://github.com/Revolutionized-IoT2/RIoT2.Core/blob/main/Connector/README.md)
([design 7.3](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/connector-sdk.md) phase 4).

- Type: ASP.NET Core service (`net10.0`), container image `ghcr.io/revolutionized-iot2/riot2-homeassistant`
- Core packages: `RIoT2.Connector.Sdk` 2.1.0 (version in `Directory.Packages.props`)
- Versions: [CHANGELOG.md](CHANGELOG.md)

## How it works

1. The connector announces itself to the orchestrator as a connector (`riot2/node/{id}/online`) and gets
   the orchestrator's URL with the configuration command. It loads the report, command and variable
   templates from there.
2. For every template it publishes a retained discovery message on Home Assistant's broker:
   `homeassistant/<component>/riot2_<connector id>/<id>/config`. It does so again when the
   templates change, when Home Assistant restarts (`homeassistant/status` = `online`) and after every
   reconnection, and it deletes its discovery messages whose template is gone.
3. Every report and variable value is published, retained, on `riot2/ha/<id>/state`. `<id>` is the
   template id, or, when it has characters other than letters, digits, `_` and `-`, those replaced by
   `_` and a short hash of the original (`set point` becomes `set_point_<hash>`).
4. A command from Home Assistant arrives on `riot2/ha/<id>/set`. The connector sends it to the
   orchestrator's `POST /api/v2/commands` (`IRiot2CommandClient`), so it goes through the outbox with a
   time-to-live and a result, like every other RIoT2 command. It never publishes RIoT2 command topics.
5. `riot2/ha/status` is `online` while the connector is connected and `offline` (its last will)
   otherwise, so Home Assistant shows the entities as unavailable.

| RIoT2 template | Home Assistant entity |
|---|---|
| Report or variable, boolean | `binary_sensor` (`true`/`false`) |
| Report or variable, number | `sensor` with `state_class: measurement`, the template's `unit` and `precision` parameters |
| Report or variable, text | `sensor` (cut to 255 characters, Home Assistant's limit) |
| Report or variable, text array | `sensor` whose state is the number of values and whose attribute `values` holds them |
| Report or variable, entity | `sensor` whose attributes are the entity's fields and whose state is the time of the last value |
| Command, boolean | `switch`; its state is the boolean report of the same device and address, else optimistic |
| Command, number | `number` (box, −1,000,000 to 1,000,000, step 0.001) |
| Command, text, entity or text array | `text` (an entity or array as JSON) |

Entities are grouped into Home Assistant devices by RIoT2 device; variables form the device
"RIoT2 variables". Unique ids, entity object ids and device identifiers are
`riot2_<connector id>_<id>`. A second connector on the same broker needs its own
`RIOT2_CONNECTOR_ID` and `RIOT2_HA_TOPIC_PREFIX`.

## Configuration

The shared connector settings (`RIOT2_MQTT_*`, `RIOT2_CONNECTOR_ID`, `RIOT2_CONNECTOR_NAME`,
`RIOT2_CONNECTOR_URL`, logging, telemetry, security) are the
[connector SDK's](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md#connectors).
The connector's own settings:

| Variable | Default | Meaning |
|---|---|---|
| `RIOT2_HA_MQTT_IP` | the RIoT2 broker | The broker Home Assistant uses. With it, also `RIOT2_HA_MQTT_PORT` (1883, 8883 with TLS), `RIOT2_HA_MQTT_USERNAME`, `RIOT2_HA_MQTT_PASSWORD`, `RIOT2_HA_MQTT_TLS` and `RIOT2_HA_MQTT_CA_FILE` |
| `RIOT2_HA_DISCOVERY_PREFIX` | `homeassistant` | Home Assistant's discovery prefix |
| `RIOT2_HA_TOPIC_PREFIX` | `riot2/ha` | Where states and availability are published and commands received |
| `RIOT2_HA_COMMAND_TTL_SECONDS` | `60` | Time-to-live of a command from Home Assistant (1–86400) |

The disk spool is off and the name is `Home Assistant` unless `RIOT2_CONNECTOR_NAME` is set. With the
optional security mode, give the connector an API key with the operator role: it sends commands.

In the compose install, add `compose.homeassistant.yml`
([deployment guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/deployment.md#home-assistant)).
In Home Assistant, add the MQTT integration with the same broker.

## Build and test

```powershell
dotnet build .\RIoT2.Connector.HomeAssistant.sln
dotnet test ..\RIoT2.Tests\RIoT2.Tests.csproj --filter FullyQualifiedName~HomeAssistantTests
```

The tests are in [RIoT2.Tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests) (`HomeAssistantTests`):
the entity mapping, states, commands, the stale-entity cleanup and the settings. `-p:CI=true`
reproduces CI. To try an unreleased Core, restore with `C:\Src\RIoT2\.localfeed` as an extra source.

## Releases

Release notes are in [CHANGELOG.md](CHANGELOG.md). Push a tag `x.y.z`: the release workflow validates and
publishes `ghcr.io/revolutionized-iot2/riot2-homeassistant:<tag>` and `:latest`.

## Contributing

- Instructions for AI coding agents: [AGENTS.md](AGENTS.md).
- Platform documentation: [.github/docs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/README.md).

## License

See [LICENSE](LICENSE).
