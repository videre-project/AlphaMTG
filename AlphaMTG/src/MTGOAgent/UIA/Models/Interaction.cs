namespace AlphaMTG.MTGOAgent.UIA.Models;

internal sealed record Interaction(
  string Source,
  string? MaterializedBy,
  string Control,
  string Operation
);
