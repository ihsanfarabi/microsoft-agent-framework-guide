using MafDemo.AgentCommon;
using P01.HelloAgent;

// Start OTel tracing first so the provider is listening before any model call.
// Disposed on exit, which flushes the spans to the console exporter.
using var telemetry = Telemetry.Start("P01.HelloAgent");

var agent = FaqBot.Create("You are HelpDeskHQ's FAQ bot. Answer IT questions in one short paragraph.");

// Default: streaming — tokens are printed as they arrive.
// Pass --one-shot to run the non-streaming path from Task 2 instead.
if (args.Contains("--one-shot"))
{
    var result = await agent.RunAsync("How do I connect to the company Wi-Fi?");
    Console.WriteLine(result);
}
else if (args.Contains("--chat"))
{
    // Stretch: interactive chat loop. Each RunStreamingAsync call is independent —
    // the agent has no session, so it won't remember earlier turns (see P03).
    Console.WriteLine("--- chat (type 'exit' to quit) ---");
    while (Console.ReadLine() is { } input && input != "exit")
    {
        await foreach (var update in agent.RunStreamingAsync(input))
            Console.Write(update.Text);
        Console.WriteLine();
    }
}
else
{
    Console.WriteLine("--- streaming ---");
    await foreach (var update in agent.RunStreamingAsync("Explain how to reset my password in 3 steps."))
        Console.Write(update.Text);
    Console.WriteLine();
}
