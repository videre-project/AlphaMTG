#!/usr/bin/env python3
"""Ask the decision model to distinguish two copies of the same card."""

import argparse
import json
from urllib.request import Request, urlopen


def request_for(island):
    cards = [{"id": number, "name": "Island", "zone": "Battlefield"} for number in (10, 20)]
    actions = [
        {"id": index, "name": "{T}: Add {U}.", "source": {"type": "card", "cardId": card["id"]}}
        for index, card in enumerate(cards)
    ]
    return {
        "model": "gliner-decide",
        "state": {"prompt": "Pay {U}.", "cards": cards, "actions": actions},
        "questions": {
            "next_action": {
                "type": "choice",
                "instructions": f"Tap Island {island} to pay for the spell.",
                "criteria": {
                    str(action["id"]): f"Island #{action['source']['cardId']}: {action['name']}"
                    for action in actions
                },
            }
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base_url", nargs="?", default="https://localhost:7101/api/plugins/alphamtg", help="Base URL of the decision API")
    args = parser.parse_args()
    url = args.base_url.rstrip("/") + "/v1/systemone"

    success = True
    for action_id, island in enumerate((10, 20)):
        request = Request(url, data=json.dumps(request_for(island)).encode(),
                          headers={"Content-Type": "application/json"})
        with urlopen(request, timeout=60) as response:
            choice = json.load(response)["answers"]["next_action"]["choice"]
        print(f"Island {island}: expected action {action_id}, model chose {choice}")
        if choice != str(action_id):
            success = False
    return 0 if success else 1


if __name__ == "__main__":
    raise SystemExit(main())
