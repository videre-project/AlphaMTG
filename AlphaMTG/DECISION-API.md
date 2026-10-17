# Duel and decision API

Use these paths under `/api/plugins/alphamtg` on Tracker's HTTP server. `GET /status` returns `connected` and the list of tracked `gameIds`. To enable local inference, set `Plugins:alphamtg:WeightsDirectory` in Tracker.

## Read a duel

`GET /snapshot?gameId=…` returns MTGOSDK's latest update for a duel. Pass `gameId` when more than one duel is open. When `canAct` is true, use the prompt to choose an entry from `actions` or `selections`.

| Snapshot field | Meaning |
| --- | --- |
| `gameId`, `turn`, `phase` | Duel and current position in the turn. |
| `capturedAtUtc` | When the client received the gameplay snapshot, in UTC. |
| `nonce`, `serverNonce` | Use `nonce` for commands. It changes with gameplay or local interaction. `serverNonce` tracks the server update and can stay the same when a dialog changes. |
| `prompt.text`, `prompt.nativeText`, `prompt.promptedPlayer` | Displayed prompt text (with native text as a fallback), native text and the name of the prompted player. |
| `interactionMode`, `canAct` | MTGO's current interaction mode and whether the local player may act. In multiplayer the prompted player also needs priority. |
| `cards`, `players` | Card IDs, names, zones and owner indices, plus player names, indices, life totals and priority. Revealed cards in hidden zones, such as a library during scry, can also appear here. |
| `actions` | Prompt options, card abilities and enabled controls. Each has an `id`, `type`, `name` and `source`. A card source identifies the card by ID and can include its `logicalAddress`. |
| `selections` | Card rows have `selectable`, `targetable` and `targeted` flags. Player rows have `targetable` and `targeted`. A card or player marked `targeted` may already be selected. |

A card's `logicalAddress` distinguishes client objects that share a card ID. Use `cardId` from the gameplay snapshot to select a card. You can inspect the live layout of a battlefield card with `GET /preview-card?cardId=…&gameId=…`. It returns the bounds, drawing order, overlapping cards, visible regions and proposed click point.

## Submit a command

Send the `nonce` from the snapshot you used to choose the action. Action IDs are numbered within each update and can refer to different actions after the nonce changes. AlphaMTG checks the nonce and `canAct` before dispatch. Read a fresh snapshot after each command to see how MTGO responded.

| Endpoint | JSON fields (all three also accept `gameId`) |
| --- | --- |
| `POST /execute-action` | `nonce`, `actionId` and any parameter required by that action |
| `POST /select-card` | `nonce`, `cardId` |
| `POST /select-player` | `nonce`, `playerName` |

Use `/execute-action` for a card ability or a prompt button. For an action that requires a parameter, use the IDs and limits on that action in `/snapshot`. Supply one of the following per request.

| Parameter | Value |
| --- | --- |
| `targetSetId` and `targetId` | A target set and one target from that set. |
| `choiceId` | An ID from the offered choices. For a mana ability, the color values are white 1, blue 2, black 4, red 8, green 16 and colorless 32. |
| `number` | A value in the offered range. |
| `order` | Each offered ID exactly once, in the desired order. |
| `distributions` | One `{targetId, value}` entry for each target. Amounts must fall within their ranges and add up to the required total. Include `targetSetId` for divided spells. Omit it for combat damage. |
| `wish` | A card name. |

Pile choices appear as separate `ChoosePile` actions. For a card action with several target sets, the response indicates whether more selections are needed.

To target with a card ability, execute its source action first, then read a fresh snapshot. Once `interactionMode` is `SelectTargets`, send the target set and target ID with the new nonce. AlphaMTG scrolls the card into view when needed and clears hover zoom before clicking. If the card is animating, wait for it to settle before retrying.

## Ask the decision model

`POST /v1/systemone` scores named questions about a shared `state` using GLiNER2.5-Decide locally. Each question runs independently. Supply a nonempty state (a string, object or array) and a `questions` object. Each question has a `type`, an `instructions` string describing the decision, and criteria appropriate to its type.

| Type | Criteria | Fields in each answer |
| --- | --- | --- |
| `choice` | Map 2–255 candidate keys to descriptions. | `choice`, `probabilities`, `confidence` |
| `noul` | Optionally supply descriptions for `true` and `false`. They default to Yes and No. | `noul`, `confidence` |
| `score` | Supply 2–255 level descriptions as an ordered array or a numeric-keyed object. Object keys are sorted and levels are numbered from zero. | `score`, `expected_level`, `probabilities`, `legend`, `confidence` |

For a choice between game actions, use the action IDs as string keys. Include a card ID in the description when two copies offer the same ability. Here, both Islands can pay for the spell.

```json
{
  "model": "gliner-decide",
  "state": {
    "prompt": "Pay {U}.",
    "cards": [
      {"id": 10, "name": "Island", "zone": "Battlefield"},
      {"id": 20, "name": "Island", "zone": "Battlefield"}
    ],
    "actions": [
      {"id": 0, "name": "{T}: Add {U}.", "source": {"type": "card", "cardId": 10}},
      {"id": 1, "name": "{T}: Add {U}.", "source": {"type": "card", "cardId": 20}}
    ]
  },
  "questions": {
    "next_action": {
      "type": "choice",
      "instructions": "Tap Island 20 to pay for the spell.",
      "criteria": {"0": "Island #10: {T}: Add {U}.", "1": "Island #20: {T}: Add {U}."}
    }
  }
}
```

The result contains `model` and an `answers` entry for each question name. If `answers.next_action.choice` is `"1"`, you can submit action 1 with the original snapshot's `nonce`. For a question about card or player selections, map the answer key back to the corresponding `cardId` or `playerName` and use the selection endpoint.

`confidence` is the difference between the two highest probabilities for `choice` and `score`. For `noul` it is `abs(2 × noul − 1)`. These scores express the model's preference among the supplied candidates. The configured limit is 512 tokens per question, including instructions, criteria and state. Invalid requests return HTTP 400, oversized inputs return 422 and missing weights return 503.

To try the Island example, run `python3 AlphaMTG/scripts/probe_game_decisions.py` with the service running and weights configured. It asks for each Island in turn and compares the returned action IDs with the expected IDs. Pass the API base URL as an argument to select an instance.

## Command logs

AlphaMTG writes one JSONL file per game. Each entry includes `at`, `state` and `request`, with `nonceAfter` when a subsequent update is available. Check `result` for a dispatched command or `note` for an error.
