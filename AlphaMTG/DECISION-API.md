# Local decision API

`POST /v1/systemone` answers named questions about a shared game state using the local `gliner-decide` ONNX model. AlphaMTG listens on port 8080 by default. Set `ALPHAMTG_ROUTER_WEIGHTS` to the directory containing `router.json` and the model files.

```json
{
  "model": "gliner-decide",
  "state": { "phase": "combat", "prompt": "Declare attackers" },
  "questions": {
    "next_action": {
      "type": "choice",
      "instructions": "The planner wants to attack with Thalia's Lieutenant. Which offered action carries that out?",
      "criteria": {
        "0": "No Response",
        "1": "Attack with Thalia's Lieutenant",
        "2": "Attack with another creature"
      }
    },
    "can_attack": {
      "type": "noul",
      "instructions": "Is attacking with Thalia's Lieutenant currently possible?"
    },
    "urgency": {
      "type": "score",
      "instructions": "How urgent is taking an action now?",
      "criteria": ["can wait", "act soon", "act immediately"]
    }
  }
}
```

`state` must be a nonempty string, object, or array. Objects and arrays are
serialized as JSON text for the local ONNX model; field names and sequence order
remain in its input. Every question gets its own `instructions`; the model runs
one pass per question. `choice` criteria are an object of outcome names to
descriptions. `score` criteria are an ordered array, numbered from zero in the
answer. Numeric-keyed score objects are also accepted: their keys are sorted
and the returned levels are reindexed from zero. `noul` optionally accepts
`{"true":"Yes description","false":"No description"}`; without it, the
two descriptions are `Yes` and `No`. Choice and score require 2–255 criteria.

The response has `model` and an `answers` object keyed by question name:

- `choice`: `choice`, `probabilities`, `confidence`
- `noul`: `noul` (the local softmax weight for `true`), `confidence`
- `score`: `score`, `expected_level`, `probabilities`, `legend`, `confidence`

Confidence is the top two probability margin for choice and score, and
`abs(2 * noul - 1)` for noul. These values come from softmax over the local
model's logits; they have **not** been calibrated as probabilities of correct
answers. The local export accepts at most 512 tokens per rendered question,
including state, instructions, and criteria. Oversized inputs return HTTP 422.
The HTTP API returns 400 for invalid requests and 503 when no weights are
configured.

`--replay` scores recorded choices using a generic question. Its agreement rate measures how often the model picks the recorded action.
