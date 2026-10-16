# AlphaMTG

AlphaMTG is a Videre Project research effort to build an autonomous agent for Magic: The Gathering Online (MTGO). We use a planning model to choose a line of play, and a smaller decision model to then translate that plan into the choices MTGO presents (e.g. from selecting a target to passing priority).

This repository implements the agent's interaction with MTGO as a [Videre Tracker](https://github.com/videre-project/Tracker) plugin. It uses [MTGOSDK](https://github.com/videre-project/MTGOSDK) to read a live duel and submit game actions. A caller to this API can ask the local GLiNER2.5-Decide model to score the available choices before submitting one.

## Build and install

Check out Tracker and MTGOSDK beside AlphaMTG and use a .NET 10 SDK. Build against the same MTGOSDK version that Tracker loads at runtime.

```sh
bash scripts/build-plugin.sh
```

To publish in Docker, run `docker compose run --rm plugin-build`.

Copy `publish/alphamtg` into the `Plugins` directory under Tracker's user data, then restart Tracker. To load plugins directly from your build, set `Plugins:Directory` to the parent `publish` directory. Keep `plugin.json` and its dependencies together. You can disable AlphaMTG by setting `enabled` to `false` in that manifest and restarting Tracker.

### Run with Tracker in Docker

With the AlphaMTG and Tracker checkouts side by side, run this command from the Tracker checkout.

```sh
docker compose -f docker-compose.yaml \
  -f ../AlphaMTG/docker/tracker-plugin.override.yaml up -d tracker-dev
```

The container reads published plugins from `AlphaMTG/publish/` and model files from `AlphaMTG/models/`.

## Use the API

Start with `GET /api/plugins/alphamtg/status` on Tracker's HTTP server to see the connection status and tracked game IDs. Use `GET /api/plugins/alphamtg/snapshot` to read a duel's prompt, cards and available actions. See the [API guide](AlphaMTG/DECISION-API.md) for command payloads, targeting and model requests. AlphaMTG writes decision logs under `PluginData/alphamtg/logs` in Tracker's user-data directory.

### Optional decision model

Set `Plugins:alphamtg:WeightsDirectory` in Tracker's configuration, or set the environment variable `Plugins__alphamtg__WeightsDirectory`. Point it to a directory containing `router.json`, the ONNX model files and the tokenizer. Relative paths resolve from the installed plugin directory. With the Docker setup above, set `ALPHAMTG_WEIGHTS_DIRECTORY` to a path such as `Z:\models\my-model`.

Only `POST /v1/systemone` requires model weights. Game inspection and commands work without model weights.

### License

AlphaMTG is licensed under the [Apache-2.0](/LICENSE) license.