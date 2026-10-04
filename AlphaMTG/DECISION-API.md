# Local decision API

`POST /v1/systemone` answers named questions about a game state using the local GLiNER2.5-Decide ONNX model. Set `ALPHAMTG_ROUTER_WEIGHTS` to the directory containing `router.json`, the tokenizer and the model. The default HTTP port is 8080.

## Choose an action

Use each action's ID from `/snapshot.actions` as a string key in `criteria`. Give a card action its card name and ability text. Include the card ID when multiple copies offer the same ability.

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

If the model returns `"1"`, pass action ID 1 and the snapshot nonce to `/execute-action`. Action IDs belong to a single snapshot. Read a fresh snapshot after dispatch.

A prompt button such as **OK** can appear as a `PromptControl` alongside native actions. Visible, enabled controls carry a group, index and client revision. Selecting a target uses a different endpoint. Take eligible cards from `/wpf-snapshot` or eligible players from `/snapshot.players` and submit the chosen identity to `/select-card` or `/select-player`. An eligible card can have no action of its own.

## Questions and results

Each question has its own instructions and shares the request's `state`, which can be a nonempty string, object or array.

| Type | Criteria | Answer |
| --- | --- | --- |
| `choice` | 2–255 keys with descriptions | Selected key and probabilities |
| `score` | 2–255 ordered levels | Selected level, expected level and probabilities |
| `noul` | Optional descriptions for `true` and `false` | Weight assigned to `true` |

For choice and score, `confidence` is the difference between the two highest softmax probabilities. For noul it is `abs(2 × noul − 1)`. Each rendered question can use up to 512 tokens, including its instructions and criteria. The endpoint returns HTTP 422 for an oversized question, 400 for invalid input and 503 when no weights are configured.

## Try the example

Run `python3 AlphaMTG/scripts/probe_game_decisions.py` with the decision service running and weights configured. It asks the model to tap each Island in turn, prints both choices and exits with a nonzero status if it picks the wrong action. It sends inference requests only.
