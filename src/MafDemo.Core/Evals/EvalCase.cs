namespace MafDemo.Core.Evals;

/// <summary>One eval row: a question and a fact the answer must contain.</summary>
public record EvalCase(string Input, string ExpectedFact);