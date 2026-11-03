# AGENTS.md — RIoT2.Connector.HomeAssistant

Applies to: this repository. Read the platform guide first:
[.github/AGENTS.md](https://github.com/Revolutionized-IoT2/.github/blob/main/AGENTS.md). It covers the
workspace map, platform-wide rules and the documentation rules. In the local workspace, every
`https://github.com/Revolutionized-IoT2/<Repo>/blob/main/<path>` link is the file
`C:\Src\RIoT2\<Repo>\<path>`; read the local file instead of fetching the URL.

## What this is

The Home Assistant connector: RIoT2 templates become Home Assistant entities through MQTT discovery,
report values their states, and commands from Home Assistant go to the orchestrator's
`POST /api/v2/commands` ([design 7.3](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/connector-sdk.md) phase 4, I47).
It is a connector-SDK service: the SDK owns the RIoT2 MQTT side, the queue, `/health` and the settings.

## Commands

Run from the workspace root (`C:\Src\RIoT2`), in PowerShell:

```powershell
dotnet build .\RIoT2.Connector.HomeAssistant\RIoT2.Connector.HomeAssistant.sln -p:CI=true
dotnet test .\RIoT2.Tests\RIoT2.Tests.csproj --filter FullyQualifiedName~HomeAssistantTests
```

CI (`ci.yml`) builds and validates the image; a tag `x.y.z` releases the image (`release.yml`).

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | `AddRIoT2Connector<HomeAssistantSink, HomeAssistantOptions>` and the Home Assistant services |
| `Services/HomeAssistantOptions.cs` | `RIOT2_HA_*` settings; `Configure` fixes the name and turns the spool off |
| `Services/HomeAssistantEntities.cs` | The mapping: templates to discovery messages, values to states, payloads to command values (pure) |
| `Services/HomeAssistantServices.cs` | `HomeAssistantConnection` (Home Assistant's broker, availability and last will), `DiscoveryPublisher` (an `IConnectorTemplateObserver`), `HomeAssistantSink`, `CommandBridge`, `HomeAssistantBridge` (routing, hosted) |

## Contracts consumed here

- [mqtt-topics.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md):
  reports and presence on the RIoT2 broker, and this connector's Home Assistant topics.
- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md): the
  template routes and `POST /api/v2/commands`.
- [env-vars.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md): the settings.

## Rules

- Commands go only through `IRiot2CommandClient` (the orchestrator's outbox); never publish
  `riot2/node/{id}/command`.
- Keep discovery messages and states retained, and the topics under the configured prefixes. Unique ids
  and object ids are `riot2_<connector id>_<id>` (`HomeAssistantEntities.ObjectId`): changing them
  duplicates entities in every Home Assistant.
- Keep `HomeAssistantEntities` free of I/O; `HomeAssistantTests` in RIoT2.Tests pins its output.
- Keep `PackageReference` items versionless; versions are in `Directory.Packages.props`.

## Pitfalls

- The connector deletes only discovery messages under its own node id (`riot2_<connector id>`); a
  changed `RIOT2_CONNECTOR_ID` leaves the old entities in Home Assistant until they are deleted there.
- A template without a matching boolean report gives an optimistic switch: Home Assistant shows the
  last state it sent, not the device's.
- Number commands have a fixed range (±1,000,000): RIoT2 command templates carry no limits.

## Related work

- [ADR 0009](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0009-connector-sdk.md): the connector SDK.
- [Design 7.1](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/reliable-delivery.md): the command outbox.
