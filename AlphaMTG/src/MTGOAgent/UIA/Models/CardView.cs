namespace AlphaMTG.MTGOAgent.UIA.Models;

internal sealed record CardView(
  string Zone,
  int ZoneInstance,
  int Index,
  string Name
);
