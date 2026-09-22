using AlphaMTG.MTGOAgent;

namespace AlphaMTG.MTGOAgent.UIA.Models;

internal sealed record ActionExecution(
  string Status,
  AgentAction Action,
  Interaction Interaction,
  AgentState State
);
