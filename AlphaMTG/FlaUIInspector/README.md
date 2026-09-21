# Inspect MTGO duel controls

To write a UI Automation index of the current duel to `mtgo-ui.json`, run this in the development checkout.

```sh
docker compose exec alphamtg run-flaui-dump --max-depth 32
```

By default, the index starts at `DuelSceneWindow` and includes visible controls, named text within components and containers with an identifiable role. Each entry can include its automation ID, name, class, control type, state, screen bounds and supported actions. Its `locator` identifies the control by its ancestors.

| Option | Effect |
| --- | --- |
| `--root all` | Start at the main MTGO window to include controls outside the duel. |
| `--verbose` | Include offscreen and unnamed elements. |
| `--max-depth` | Set how far to descend from the root. |
| `--minified` | Emit JSON without indentation. |

To inspect the Keep button in a solitaire prompt, use its automation ID.

```sh
docker compose exec alphamtg run-flaui-dump \
  --action inspect --within PromptBoxView_Solitaire --target KeepButton
```

To press it, change `inspect` to `invoke`. Other available operations depend on the control's `actions` list and include `toggle`, `select`, `setValue`, `setRangeValue`, `expand` and `collapse`. Both setters require `--value`.
