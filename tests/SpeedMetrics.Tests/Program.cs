using System.Text.Json;
using Codex.SpeedMetrics;

HostTokenizer.Initialize();
int checks = 0;
void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); }
string Row(string record, object payload, long at = 0) => JsonSerializer.Serialize(new {
    type = record, timestamp = DateTimeOffset.FromUnixTimeMilliseconds(at).ToString("O"), payload });
void Begin(ResponseSpeedAnalyzer a, string turn = "turn-1", long at = 0) =>
    a.ObserveLine(Row("event_msg", new { type = "task_started", turn_id = turn }, at));
void Item(ResponseSpeedAnalyzer a, long start, long end, string kind = "Reasoning") =>
    a.ObserveLine(Row("event_msg", new { type = "item_completed", item = new { type = kind }, started_at_ms = start, completed_at_ms = end }, end));
void Usage(ResponseSpeedAnalyzer a, long at, int count, string response = "r1", string turn = "turn-1") =>
    a.ObserveLine(Row("token_usage_record", new { response_id = response, turn_id = turn, usage = new { output_tokens = count, reasoning_output_tokens = count / 2 } }, at));

var speed = new ResponseSpeedAnalyzer(); Begin(speed); Item(speed, 1000, 1100); Usage(speed, 2000, 100);
Check(speed.AverageTps == 100, "tool arguments after the short reasoning item remain inside the response clock");
Check(speed.TimedMilliseconds == 1000, "completion clock reaches official response usage, not reasoning completion");
Usage(speed, 2000, 100);
speed.ObserveLine(Row("event_msg", new { type = "token_count", info = new { last_token_usage = new { output_tokens = 100 } } }, 2010));
Check(speed.CompletedResponses == 1 && speed.TimedTokens == 100, "response-id duplicates and token-count mirrors are not double counted");
Item(speed, 5000, 5100); Usage(speed, 6000, 100, "r2");
Check(speed.AverageTps == 100 && speed.TimedMilliseconds == 2000, "the tool execution gap between two requests is excluded");
Begin(speed, "turn-2", 7000);
Check(speed.AverageTps is null && speed.CompletedResponses == 0, "new turn does not inherit a previous average");
Usage(speed, 9000, 75, "r3", "turn-2");
Check(speed.AverageTps is null && speed.CompletedResponses == 1 && speed.TimedResponses == 0, "missing first-output timing stays unavailable instead of inventing a denominator");
Item(speed, 10000, 10100); Usage(speed, 11000, 50, "r4", "turn-2");
Check(speed.AverageTps == 50 && speed.TimedResponses == 1 && speed.CompletedResponses == 2, "partial timing exposes response coverage");
var overlap = new ResponseSpeedAnalyzer(); Begin(overlap); Item(overlap, 1000, 1500); Item(overlap, 1200, 1800, "AgentMessage"); Usage(overlap, 2000, 100);
Check(overlap.AverageTps == 100, "overlapping output items do not add durations twice");
var invalid = new ResponseSpeedAnalyzer(); Begin(invalid); Item(invalid, 2000, 1000); Usage(invalid, 1500, 100);
Check(invalid.AverageTps is null, "invalid timestamps do not produce speed");

const string text = "Hello, tokenizer! 你好，世界。A token is not a packet. 🚀 streaming text changes BPE boundaries.";
var whole = new VisibleTextWindow(); whole.Observe("m", text, 1000);
var fragmented = new VisibleTextWindow();
for (var i = 1; i <= text.Length; i++) fragmented.Observe("m", text[..i], 1000 + i);
Check(whole.Read(1500) == VisibleTextWindow.Count(text) / 2d, "live uses BPE token estimates, not bytes or stream events");
Check(fragmented.Read(1500) == whole.Read(1500), "one packet and many packets produce the same estimated count");
fragmented.Observe("m", text, 1501);
Check(fragmented.Read(1502) == whole.Read(1500), "duplicate stream text is not counted again");
var baseline = new VisibleTextWindow(); baseline.Observe("m", text, 1000, true);
Check(baseline.Read(1100) is null, "a history snapshot does not become a live burst");
baseline.Observe("m", text + " extra output", 1200);
Check(baseline.Read(1500) == Math.Max(0, VisibleTextWindow.Count(text + " extra output") - VisibleTextWindow.Count(text)) / 2d, "resumed streams count only newly received text");
baseline.Clear();
Check(baseline.Read(2000) is null, "disconnect and conversation change clear estimates");
Check(whole.Read(4000) == 0, "old text expires from the two-second window");
var isolated = new VisibleTextWindow(); isolated.Observe("other", "other conversation", 1000);
Check(whole.Read(4001) == 0, "separate conversations cannot leak samples into one another");
var date = new DateOnly(2026, 11, 1);
UsageEntry E(string id, long input, long cache, long output, string model = "gpt-6.1-sol") =>
    new(id, "thread", "turn", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), model, input, cache, 0, output, output / 2);
var total = UsageTotals.Sum(new[] { E("a", 1000, 900, 100) });
Check(total.Total == 1100 && total.CachePercent == 90 && total.Reasoning == 50, "cached input and reasoning are subsets, never added to total again");
Check(total.KnownApiUsd == .00129m, "API cost separates cached input from uncached input");
Check(ApiEquivalent.Price(E("b", 272000, 0, 100)) == .545m, "272K boundary stays at standard short-context rate");
Check(ApiEquivalent.Price(E("c", 272001, 0, 100)) == 1.089504m, "long-context surcharge applies to the entire qualifying request");
Check(ApiEquivalent.Price(E("d", 100, 0, 100, "unknown")) is null, "unknown model is not assigned a guessed price");
Check(ApiEquivalent.Price(E("e", 100, 0, 100) with { CacheWrite = 10 }) is null, "unknown cache-write TTL is not assigned a guessed price");
var twoSmall = UsageTotals.Sum(new[] { E("s1", 200000, 0, 100), E("s2", 200000, 0, 100) });
Check(twoSmall.KnownApiUsd == .802m, "cumulative task size does not trigger long-context surcharge");
var yesterday = E("old", 50, 0, 10) with { At = new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero) };
var merged = UsageMerge.Combine(new Dictionary<DateOnly, long> { [date.AddDays(-1)] = 1000 }, new[] { yesterday, E("today", 100, 0, 10) }, date);
Check(merged.Sum(d => d.Tokens) == 1110 && merged[0].Source == "官方", "official history replaces local history instead of doubling it");
merged = UsageMerge.Combine(new Dictionary<DateOnly, long> { [date] = 80 }, new[] { E("today", 100, 0, 10) }, date);
Check(merged.Single().Tokens == 110, "overlapping current-day official and local data are never added together");
Check(UsageMerge.Monday(date) == new DateOnly(2026, 10, 26), "week begins Monday even across month boundaries");
var testRoot = Path.Combine(Path.GetTempPath(), "codex-usage-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(testRoot, "sessions"));
try
{
    string UsageRow(string id) => Row("token_usage_record", new { response_id = id, thread_id = "thread", turn_id = "turn",
        usage = new { input_tokens = 100, cached_input_tokens = 90, output_tokens = 10, reasoning_output_tokens = 5 } }, 1000);
    var file = Path.Combine(testRoot, "sessions", "test.jsonl");
    File.WriteAllText(file, Row("turn_context", new { model = "gpt-6.1-sol", turn_id = "turn" }) + "\n" + UsageRow("r1") + "\n");
    var ledger = new UsageLedger(); ledger.Refresh(Path.GetDirectoryName(file)!);
    Check(ledger.Entries.Count == 1 && UsageTotals.Sum(ledger.Entries).Total == 110, "ledger reads official per-response usage");
    File.Copy(file, Path.Combine(testRoot, "sessions", "fork.jsonl"));
    ledger.Refresh(Path.GetDirectoryName(file)!);
    Check(ledger.Entries.Count == 1, "same response copied into a fork is counted once globally");
    File.AppendAllText(file, UsageRow("r2")); ledger.Refresh(Path.GetDirectoryName(file)!);
    Check(ledger.Entries.Count == 1, "incomplete append waits for a newline");
    File.AppendAllText(file, "\n"); ledger.Refresh(Path.GetDirectoryName(file)!);
    Check(ledger.Entries.Count == 2, "completed append is counted once without rescanning old usage");
    ledger.Refresh(Path.GetDirectoryName(file)!);
    Check(ledger.Entries.Count == 2, "repeated background polling does not multiply tokens");
    File.WriteAllText(file, UsageRow("r3") + "\n"); ledger.Refresh(Path.GetDirectoryName(file)!);
    Check(ledger.Entries.Select(e => e.Id).OrderBy(x => x).SequenceEqual(new[] { "r1", "r3" }), "file truncation rebuilds only the affected file");
}
finally { Directory.Delete(testRoot, true); }
var heldDisplay = new LiveSpeedDisplay();
var reading = new SpeedReading("chat-a", "turn-a", true, 40, 100, 2500, 1, 1, 40, 100, 2500, 40,
    60, true, 1000, false);
Check(heldDisplay.Read(reading, 1000) == (60d, false), "new streamed text updates the displayed live rate");
Check(heldDisplay.Read(reading with { LiveEstimateTps = 30 }, 1700).Rate == 60,
    "window decay without new text does not drag the last active rate downward");
Check(heldDisplay.Read(reading with { LiveEstimateTps = 0 }, 4000) == (60d, true),
    "tool execution holds and labels the last rate instead of displaying zero");
Check(heldDisplay.Read(reading with { LiveEstimateTps = 70, LastOutputAtMs = 4100 }, 4100) == (70d, false),
    "new output replaces the held rate immediately");
Check(heldDisplay.Read(reading with { LiveEstimateTps = 0, LastOutputAtMs = null, TurnId = "turn-b" }, 5000).Rate is null,
    "new turn clears the previous turn's held reading");
heldDisplay.Read(reading, 1000);
Check(heldDisplay.Read(reading with { ThreadId = "chat-b", LastOutputAtMs = null }, 2000).Rate is null,
    "changing conversations cannot carry over the old rate");
heldDisplay.Read(reading, 1000);
Check(heldDisplay.Read(reading with { LiveConnected = false }, 2000).Rate is null,
    "disconnect clears stale held telemetry");
const string officialPricePage = "# Model\n| Metric | Price | Unit |\n| Input | $2 | 1M tokens |\n| Cached input | $0.1 | 1M tokens |\n| Output | $10 | 1M tokens |\n- Prompts with more than 272K input tokens are priced at 2x input and cache rates and 1.5x output for the full request.\n";
var parsedPrice=ModelPriceParser.Parse("gpt-new-model",officialPricePage,DateTimeOffset.UtcNow);
var withCacheParagraph=ModelPriceParser.Parse("gpt-new-model","- Cached input tokens are priced at 5% of the uncached input token rate.\n"+officialPricePage,DateTimeOffset.UtcNow);
Check(withCacheParagraph.LongThreshold==272000,"cache-discount prose is not mistaken for a long-context threshold");
Check(parsedPrice.Input==2 && parsedPrice.Cached==.1m && parsedPrice.Output==10 && parsedPrice.LongThreshold==272000,
    "official model page parses explicit per-million prices and threshold");
Check(parsedPrice.LongCachedMultiplier==2 && parsedPrice.LongOutputMultiplier==1.5m,
    "long-context cache and output multipliers come from official wording");
var noCacheRule=ModelPriceParser.Parse("gpt-new-model",officialPricePage.Replace("input and cache rates","input"),DateTimeOffset.UtcNow);
Check(ApiEquivalent.Price(E("c",300000,200000,100,"gpt-new-model"),new Dictionary<string,ModelPrice>{{"gpt-new-model",noCacheRule}}) is null,
    "incomplete long-context cache rule remains unpriced");
var modifiedPrice=ModelPriceParser.Parse("gpt-new-model",officialPricePage.Replace("$2 |","$3 |"),DateTimeOffset.UtcNow);
Check(ApiEquivalent.Price(E("price-change",100000,0,0,"gpt-new-model"),new Dictionary<string,ModelPrice>{{"gpt-new-model",modifiedPrice}})==.3m,
    "a newly published official rate changes the model's estimate");
bool rejectsSchema=false;try{ModelPriceParser.Parse("gpt-new-model",officialPricePage.Replace("1M tokens","1K tokens"),DateTimeOffset.UtcNow);}catch(FormatException){rejectsSchema=true;}
Check(rejectsSchema,"changed pricing units fail closed instead of creating a thousand-fold error");
bool rejectsDuplicate=false;try{ModelPriceParser.Parse("gpt-new-model",officialPricePage+"| Input | $99 | 1M tokens |\n",DateTimeOffset.UtcNow);}catch(FormatException){rejectsDuplicate=true;}
Check(rejectsDuplicate,"ambiguous multiple price tables are not guessed");
Check(Math.Abs(ResponseSpeedAggregate.Average(new[]{reading with{TimedOutputTokens=100,TimedMilliseconds=1000},reading with{TimedOutputTokens=100,TimedMilliseconds=10000}})!.Value-200000d/11000)<1e-10,
    "multi-task mean divides combined timed output by combined duration, not a sum of speeds");
Check(ResponseSpeedAggregate.Average(new[]{reading with{TimedMilliseconds=0}}) is null,"aggregate with no valid timing is unknown, not zero");
var longLog=Path.Combine(Path.GetTempPath(),"codex-long-turn-"+Guid.NewGuid().ToString("N")+".jsonl");
try
{
    var first=Row("event_msg",new{type="task_started",turn_id="turn-1"})+"\n"
        +Row("event_msg",new{type="item_completed",item=new{type="Reasoning"},started_at_ms=1000,completed_at_ms=1100},1100)+"\n"
        +Row("token_usage_record",new{response_id="long-a",turn_id="turn-1",usage=new{output_tokens=100}},2000)+"\n";
    File.WriteAllText(longLog,first+"{\"type\":\"response_item\",\"payload\":\""+new string('x',9*1024*1024)+"\"}\n"
        +Row("event_msg",new{type="item_completed",item=new{type="Reasoning"},started_at_ms=3000,completed_at_ms=4000},4000)+"\n"
        +Row("token_usage_record",new{response_id="long-b",turn_id="turn-1",usage=new{output_tokens=100}},13000)+"\n");
    using var observer=new SpeedObserver(false);var longReading=observer.Read("long-chat",longLog);
    Check(longReading.TimedOutputTokens==200 && longReading.TimedMilliseconds==11000 && !longReading.PartialHistory,
        "restart includes the whole current turn even after more than 8 MB of tool output");
}
finally{File.Delete(longLog);}

// Conservation checks use unheld one-second display samples, including the filter tail.
void IntegralCase(string label, List<(long At, string Id, string Text)> events, int expected)
{
    var window = new VisibleTextWindow();
    var position = 0; double area = 0; double peak = 0;
    var finish = (events.Max(e => e.At) / 1000 + 5) * 1000;
    for (long second = 1000; second <= finish; second += 1000)
    {
        while (position < events.Count && events[position].At <= second)
        {
            var e = events[position++];
            var complete = !events.Skip(position).Any(next => next.Id == e.Id);
            window.Observe(e.Id, e.Text, e.At, complete: complete);
        }
        var rate = window.ReadDisplayRate(second) ?? 0;
        Check(double.IsFinite(rate) && rate >= 0, label + " finite one-second sample");
        area += rate; peak = Math.Max(peak, rate);
    }
    var error = expected == 0 ? Math.Abs(area) : Math.Abs(area - expected) / expected;
    Check(error <= .05, $"{label} integrated display error {error:P4} <= 5% (area={area}, tokens={expected})");
    Check(peak <= Math.Max(expected, 1) / 2d, label + " cannot divide by a tiny packet interval");
}
IntegralCase("slow tokenizer boundary correction", new(){(1,"url","http:/"),(5000,"url","http://")}, VisibleTextWindow.Count("http://"));
foreach(var word in new[]{"function", "establishment", "abcdefghijklmnopqrstuvwxyz", "exception", "processing", "こんにちは"})
for(var cut=1;cut<word.Length;cut++)
    if(VisibleTextWindow.Count(word[..cut])>VisibleTextWindow.Count(word))
        IntegralCase("delayed BPE merge "+word+cut,new(){(1,"word",word[..cut]),(5000,"word",word)},VisibleTextWindow.Count(word));
IntegralCase("one short burst", new(){(1000,"one","Yes.")}, VisibleTextWindow.Count("Yes."));
IntegralCase("bursty chunks and long tool gap", new(){(1,"a",text),(1,"b",text),(999,"c",text),(20000,"d",text)},4*VisibleTextWindow.Count(text));
var chunks = Enumerable.Range(1,text.Length).Select(i=>((long)i*79,"fragment",text[..i])).ToList();
IntegralCase("real BPE fragmented Unicode",chunks,VisibleTextWindow.Count(text));
var random = new Random(1701);
for (var scenario=0; scenario<8; scenario++)
{
    long at=0;var events=new List<(long At,string Id,string Text)>();int expectedTotal=0;
    for(var i=0;i<60;i++){at+=random.Next(0,900);var value=string.Join(" ",Enumerable.Repeat("output",random.Next(1,50)));events.Add((at,"item-"+i,value));expectedTotal+=VisibleTextWindow.Count(value);}
    IntegralCase("random batching "+scenario,events,expectedTotal);
}
var oneSecond = new LiveSpeedDisplay();
Check(oneSecond.Read(reading,1000).Rate == 60,"first live display is available");
Check(oneSecond.Read(reading with {LiveEstimateTps=999,LastOutputAtMs=1100},1100).Rate==60,"display cannot jump several times within one second");
Check(oneSecond.Read(reading with {LiveEstimateTps=75,LastOutputAtMs=1900},2000).Rate==75,"next second publishes the new window rate");

using (var replay = new CodexLiveStream())
{
    var threadA = "00000000-0000-0000-0000-000000000001";
    var threadB = "00000000-0000-0000-0000-000000000002";
    replay.SetThreads(new[]{threadA,threadB});
    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    typeof(CodexLiveStream).GetField("_connected",flags)!.SetValue(replay,true);
    var process = typeof(CodexLiveStream).GetMethod("ProcessMessage",flags)!;
    void Feed(string thread, object change)
    {
        using var message = JsonDocument.Parse(JsonSerializer.Serialize(new {method="thread-stream-state-changed",
            @params=new {hostId="local",conversationId=thread,change}}));
        process.Invoke(replay,new object[]{message.RootElement});
    }
    object Snapshot(string value) => new {type="snapshot",revision=1,conversationState=new {
        turns=new[]{new {turnId="turn",turnStartedAtMs=1000,status="inProgress",items=new[]{new {type="agentMessage",id="same-item",text=value}}}}}};
    object Patch(string value,int revision=2,int baselineRevision=1) => new {type="patches",revision,baseRevision=baselineRevision,
        patches=new[]{new {op="replace",path=new object[]{"turns",0,"items",0,"text"},value}}};
    Feed(threadA,Snapshot("old A"));Feed(threadB,Snapshot("old B"));
    Check(replay.Read(threadA).LastOutputAtMs is null && replay.Read(threadB).LastOutputAtMs is null,"both subscribed snapshots are baselines, not bursts");
    Feed(threadA,Patch("old A new output streaming words arrive now"));
    Check(replay.Read(threadA).LastOutputAtMs is not null && replay.Read(threadB).LastOutputAtMs is null,"A output never changes B, even with identical turn/item IDs");
    Feed(threadB,Patch("old B independent output streaming words arrive now"));
    Check(replay.Read(threadB).LastOutputAtMs is not null,"B receives its own independent output");
    replay.SetThreads(new[]{threadB});Feed(threadA,Patch("old A more output",3,2));
    Check(!replay.Read(threadA).Connected && replay.Read(threadB).Connected,"late A frames cannot reselect an unsubscribed conversation");
    Feed(threadB,Patch("bad revision",5,3));
    Check(replay.Read(threadB).LastOutputAtMs is null,"missing revision clears a potentially incomplete live window");
    Feed(threadB,Snapshot("")); Feed(threadB,Patch("function"));
    Check(replay.Read(threadB).LastOutputAtMs is null,"unfinished short word stays pending instead of generating unstable counts");
    Feed(threadB,new {type="patches",revision=3,baseRevision=2,patches=new[]{new {op="add",
        path=new object[]{"turns",0,"agentMessageCompletedAtMsById","same-item"},value=1234}}});
    Check(replay.Read(threadB).LastOutputAtMs is not null,"completion metadata flushes the unchanged short message tail");

}
Console.WriteLine($"RESULT {checks} checks passed");
