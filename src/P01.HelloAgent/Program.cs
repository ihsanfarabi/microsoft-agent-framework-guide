using P01.HelloAgent;

var agent = FaqBot.Create("You are HelpDeskHQ's FAQ bot. Answer IT questions in one short paragraph.");

// Default: streaming — tokens are printed as they arrive.
// Pass --one-shot to run the non-streaming path from Task 2 instead.
if (args.Contains("--one-shot"))
{
    var result = await agent.RunAsync("How do I connect to the company Wi-Fi?");
    Console.WriteLine(result);
}
else
{
    Console.WriteLine("--- streaming ---");
    await foreach (var update in agent.RunStreamingAsync("Explain how to reset my password in 3 steps."))
        Console.Write(update.Text);
    Console.WriteLine();
}