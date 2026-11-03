# Changelog

All notable changes to `RIoT2.Connector.HomeAssistant`. A version is released by pushing a git tag; CI
then publishes the image `ghcr.io/revolutionized-iot2/riot2-homeassistant`.

## [Unreleased]

## [1.0.0] - 2026-11-03

The first release ([I47](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-3-4.md#i47-home-assistant-connector-m),
[design 7.3](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/connector-sdk.md) phase 4), on `RIoT2.Connector.Sdk` 2.1.0.

- Home Assistant MQTT discovery for every report, variable and command template: binary sensors and
  sensors (unit and precision from the template, an entity's fields as attributes), switches (with the
  state of the matching report), numbers and texts, grouped by RIoT2 device.
- Report and variable values as retained states; availability with a last will.
- Commands from Home Assistant through the orchestrator's `POST /api/v2/commands`, with
  `RIOT2_HA_COMMAND_TTL_SECONDS`.
- Discovery messages republished when the templates change, when Home Assistant restarts and after a
  reconnection; stale ones of this connector are deleted.
- Settings `RIOT2_HA_MQTT_*` (Home Assistant's broker, the RIoT2 broker by default),
  `RIOT2_HA_DISCOVERY_PREFIX` and `RIOT2_HA_TOPIC_PREFIX`.
