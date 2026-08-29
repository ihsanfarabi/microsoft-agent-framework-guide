using P01.HelloAgent;

var agent = FaqBot.Create("You are HelpDeskHQ's FAQ bot. Answer IT questions in one short paragraph.");
var result = await agent.RunAsync("How do I connect to the company Wi-Fi?");
Console.WriteLine(result);