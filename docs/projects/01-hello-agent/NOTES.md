# P01: HelloAgent — Learning Notes

## The experiment

Same prompt, same model, same code path (`--one-shot`, `agent.RunAsync`) — only the `instructions` string passed to `FaqBot.Create` changed:

- **Run A (default):** `"You are HelpDeskHQ's FAQ bot. Answer IT questions in one short paragraph."`
- **Run B (alternate):** `"Answer only in bullet points, max 3."` — produced by temporarily editing `Program.cs` to pass this alternate string to `FaqBot.Create`; the committed `Program.cs` carries only the default (Run A) instructions, so re-running Run B from the tree means repeating that one-line edit.

Prompt for both: `How do I connect to the company Wi-Fi?`

### Run A output (default instructions)

```
To connect to the company Wi-Fi, open the Wi-Fi settings on your device and select the corporate network (look for the SSID posted in your onboarding packet or the office sign—typically "CompanyNet" for employees and "CompanyNet-Guest" for visitors). Sign in with your corporate username and password, and if prompted, accept the security certificate or complete the network portal page. If you don't see the network, your device isn't enrolled in mobile device management (MDM), or your credentials are rejected, restart your device first and then contact the Help Desk for assistance—please have your employee ID ready.
```

### Run B output (alternate instructions)

```
• Open your device's Wi-Fi settings and select the company network (ask IT for
  the network name/SSID if you're unsure)
• Enter the Wi-Fi password or sign in with your company credentials
  (username/password) when prompted
• If the network requires a portal login, certificate, or VPN, follow the
  on-screen prompts or contact your IT department for assistance
```

## What I learned

- **The agent loop:** `ChatClientAgent.RunAsync` wraps a single model call in an internal `orchestrate_tools` step — even with zero tools registered, the framework still runs the orchestration span as the parent of the chat call, so the "loop" here is one turn: instructions + prompt in, one completion out, no tool round-trips.
- **What the trace showed:** each run emits exactly two spans from source `Experimental.Microsoft.Agents.AI` — an `orchestrate_tools` span (Kind: Internal) parenting a `chat glm-5.3-flash:cloud` span (Kind: Client, ~4.07s vs ~3.20s wall time; the link is the child's ParentSpanId equal to the parent's SpanId within a shared TraceId) — and the chat span carries the GenAI semantics: `gen_ai.request.model`, `gen_ai.response.finish_reasons: ["stop"]`, and token usage (Run A: 40 input / 456 output; Run B: 33 input / 333 output). The token counts, timings, and wall-clock figures recorded in these notes are single samples from one local run — they vary a little on every re-run, so don't expect them to reproduce exactly.
- **What instructions changed:** the model followed the format constraint exactly — one prose paragraph vs three bullets, nothing more — and the shorter instructions also measurably shrank both sides of the call: input tokens dropped 40 → 33 (shorter system prompt) and output tokens dropped 456 → 333 (~27% less text), so a tighter instruction is a direct lever on latency and token cost, not just tone.
