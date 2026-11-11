using System.Text.Json;
using Codex.SpeedMetrics;

HostTokenizer.Initialize();
int checks = 0;
void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); }

using (var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","harness-throughput-0.2.1-rc.5.json"))))
foreach (var fixture in fixtures.RootElement.EnumerateArray())
{
    var projection = new HarnessThroughputProjection();
    foreach (var e in fixture.GetProperty("events").EnumerateArray()) projection.Observe(e);
    var expected = fixture.GetProperty("expected"); var name = fixture.GetProperty("name").GetString();
    Check(projection.DecodeTokens == expected.GetProperty("tokens").GetDouble() && projection.DecodeMilliseconds == expected.GetProperty("ms").GetDouble(), "Harness native golden tokens/time: " + name);
    Check(expected.GetProperty("tps").ValueKind == JsonValueKind.Null ? projection.Tps is null
        : projection.Tps is double value && Math.Abs(value-expected.GetProperty("tps").GetDouble())<1e-8, "Harness native golden throughput: " + name);
    Check(projection.LlmMilliseconds == expected.GetProperty("llmMs").GetDouble() && projection.TtftMilliseconds == expected.GetProperty("ttftMs").GetDouble()
        && projection.TtftSteps == expected.GetProperty("ttftSteps").GetInt32(), "Harness native golden timing boundaries: " + name);
}

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

var zeroOutput = new ResponseSpeedAnalyzer(); Begin(zeroOutput); Item(zeroOutput,1000,1100); Usage(zeroOutput,2000,100,"z1");
Item(zeroOutput,3000,3100); Usage(zeroOutput,4000,0,"z2");
Check(zeroOutput.SessionTimedTokens==100 && zeroOutput.SessionTimedMilliseconds==2000 && zeroOutput.SessionTimedResponses==2,
    "zero-output completed response retains its generation time like Harness");
var staleTurn = new ResponseSpeedAnalyzer(); Begin(staleTurn,"old",0); Begin(staleTurn,"new",1000);
staleTurn.ObserveLine(Row("event_msg",new{type="item_completed",turn_id="old",item=new{type="Reasoning"},started_at_ms=1500,completed_at_ms=1600},1600));
Usage(staleTurn,2000,100,"stale-response","new");
Check(staleTurn.SessionTimedResponses==0,"late item from another turn cannot establish this response's start");
new ResponseSpeedAnalyzer().ObserveLine("{\"type\":\"token_usage_record\",\"payload\":\"bad\"}");
new ResponseSpeedAnalyzer().ObserveLine("null");
Check(true,"malformed record shapes do not stop the incremental parser");
var rewrittenLog=Path.Combine(Path.GetTempPath(),"speed-rewrite-"+Guid.NewGuid().ToString("N")+".jsonl");
try
{
    var prefix=Row("event_msg",new{type="task_started",turn_id="turn-1"})+"\n"
        +Row("event_msg",new{type="item_completed",item=new{type="Reasoning"},started_at_ms=1000,completed_at_ms=1100},1100)+"\n";
    string Record(int n)=>Row("token_usage_record",new{response_id="same",turn_id="turn-1",usage=new{output_tokens=n}},2000)+"\n";
    File.WriteAllText(rewrittenLog,prefix+Record(100));using var observed=new SpeedObserver(false);observed.Read("rewrite",rewrittenLog);
    File.WriteAllText(rewrittenLog,prefix+Record(900));File.SetLastWriteTimeUtc(rewrittenLog,DateTime.UtcNow.AddSeconds(2));
    Check(observed.Read("rewrite",rewrittenLog).TimedOutputTokens==900,"same-length rewrite invalidates cached usage");
    File.WriteAllText(rewrittenLog,prefix+Row("event_msg",new{type="token_count",info=new{last_token_usage=new{output_tokens=100},total_token_usage=new{output_tokens=100}}},2000)+"\n"
        +Record(100)+"{\"type\":\"response_item\",\"payload\":\""+new string('x',9*1024*1024)+"\"}\n");
    using var mixedCarrier=new SpeedObserver(false);
    Check(mixedCarrier.Read("mixed",rewrittenLog).CompletedResponses==1,"modern usage before a large tool tail suppresses legacy mirrors");
}
finally{File.Delete(rewrittenLog);}

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
var throughputDisplay = new ResponseSpeedDisplay();
var firstResponse = reading with { TimedOutputTokens = 100, TimedMilliseconds = 500, LiveEstimateTps = 2.25 };
Check(throughputDisplay.Read(firstResponse).Rate == 200, "Harness throughput uses provider tokens and response time, never the visible-text estimate");
var weightedResponses = firstResponse with { TimedOutputTokens = 1000, TimedMilliseconds = 9500 };
Check(Math.Abs(throughputDisplay.Read(weightedResponses).Rate!.Value - 1000 / 9.5) < .00001,
    "completed-response throughput weights duration, not the arithmetic mean of 200 and 100");
Check(throughputDisplay.Read(weightedResponses with { Activity = "command" }).Rate == throughputDisplay.Read(weightedResponses).Rate,
    "tool execution cannot decay completed-response throughput");
Check(throughputDisplay.Read(firstResponse with { ThreadId = "other", TimedOutputTokens = 0, TimedMilliseconds = 0 }).Rate is null,
    "unknown chat never inherits another chat's throughput");
Check(new ResponseSpeedDisplay().Read(reading with { TimedMilliseconds = 0 }).Rate is null,
    "missing timing never invents a rate from usage or live estimates");
var sessionSpeed = new ResponseSpeedAnalyzer();
Begin(sessionSpeed, "first", 0); Item(sessionSpeed, 1000, 1200); Usage(sessionSpeed, 1500, 100, "first-response", "first");
Begin(sessionSpeed, "second", 60000); Item(sessionSpeed, 70000, 71000); Usage(sessionSpeed, 79000, 900, "second-response", "second");
Check(sessionSpeed.SessionTimedTokens == 1000 && sessionSpeed.SessionTimedMilliseconds == 9500,
    "session throughput spans turns but excludes idle, tool and pre-output waiting intervals");
Usage(sessionSpeed, 80000, 500, "untimed", "second");
Check(sessionSpeed.SessionTimedTokens == 1000 && sessionSpeed.SessionTimedMilliseconds == 9500
    && sessionSpeed.SessionCompletedResponses == 3 && sessionSpeed.SessionTimedResponses == 2,
    "untimed response is excluded from BOTH sides of the ratio and recorded in coverage");
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

var backgroundLog = Path.Combine(Path.GetTempPath(), "codex-background-throughput-" + Guid.NewGuid().ToString("N") + ".jsonl");
try
{
    File.WriteAllText(backgroundLog,
        Row("event_msg", new { type = "task_started", turn_id = "one" }, 0) + "\n"
        + Row("event_msg", new { type = "item_completed", item = new { type = "Reasoning" }, started_at_ms = 1000, completed_at_ms = 1200 }, 1200) + "\n"
        + Row("token_usage_record", new { response_id = "one", turn_id = "one", usage = new { output_tokens = 100 } }, 1500) + "\n"
        + "{\"type\":\"response_item\",\"payload\":\"" + new string('x', 32 * 1024 * 1024) + "\"}\n"
        + Row("event_msg", new { type = "task_started", turn_id = "two" }, 60000) + "\n"
        + Row("event_msg", new { type = "item_completed", item = new { type = "Reasoning" }, started_at_ms = 70000, completed_at_ms = 71000 }, 71000) + "\n"
        + Row("token_usage_record", new { response_id = "two", turn_id = "two", usage = new { output_tokens = 900 } }, 79000) + "\n");
    using var background = new SpeedObserver(); // No Watch: exercises background history without connecting IPC.
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var pending = background.Read("synthetic", backgroundLog);
    var firstReadMs = timer.Elapsed.TotalMilliseconds;
    while (pending.CompletedResponses < 2 && timer.Elapsed < TimeSpan.FromSeconds(10))
    { Thread.Sleep(10); pending = background.Read("synthetic", backgroundLog); }
    Check(pending.TimedOutputTokens == 1000 && pending.TimedMilliseconds == 9500 && pending.CompletedResponses == 2,
        "background full-session restore includes both turns across a 32 MiB tool result");
    Console.WriteLine($"BENCH background 32 MiB first read {firstReadMs:F2} ms; complete {timer.Elapsed.TotalMilliseconds:F2} ms");
    var retained = new ResponseSpeedDisplay().Read(pending).Rate;
    Check(retained.HasValue && Math.Abs(retained.Value - 1000 / 9.5) < .00001,
        "background restore publishes the same weighted throughput as the Harness definition");
}
finally { File.Delete(backgroundLog); }

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
    Feed(threadB,new {type="snapshot",revision=10,conversationState=new {turns=new[]{new {turnId="turn",turnStartedAtMs=1000,status="inProgress",
        items=new[]{new {type="commandExecution",id="command",status="inProgress",startedAtMs=1000,completedAtMs=0}}}}}});
    Check(replay.Read(threadB).ToolRunning,"live IPC command item sets the actual tool-running state");
    Feed(threadB,new {type="patches",revision=11,baseRevision=10,patches=new[]{new {op="replace",path=new object[]{"turns",0,"items",0,"status"},value="completed"}}});
    Check(!replay.Read(threadB).ToolRunning,"tool completion patches clear the hold phase");

    // Direct official IPC counters must be consumed, independently of text estimates.
    object UsageSnapshot(int revision, long input, long output) => new { type = "snapshot", revision,
        conversationState = new { latestTokenUsageInfo = new { total = new { inputTokens = input, outputTokens = output,
            cachedInputTokens = input / 2, reasoningOutputTokens = output / 2, totalTokens = input + output } }, turns = Array.Empty<object>() } };
    Feed(threadB, UsageSnapshot(40, 1000, 500));
    Check(replay.ReadUsage(threadB) == new LiveUsageTotals(1000, 500, 500, 250, 1500), "IPC snapshot exposes official counters without waiting for log parsing");
    Check(replay.ReadUsage(threadA) is null, "unsubscribed conversation cannot leak usage into the selected chat");
    Feed(threadB, new { type = "patches", revision = 41, baseRevision = 40, patches = new[] { new { op = "replace",
        path = new object[] { "latestTokenUsageInfo", "total" }, value = new { inputTokens = 1500, outputTokens = 600,
            cachedInputTokens = 700, reasoningOutputTokens = 300, totalTokens = 2100 } } } });
    Check(replay.ReadUsage(threadB)?.Total == 2100 && replay.ReadUsage(threadB)?.Output == 600, "nested usage patches update official counters atomically");
    Feed(threadB, new { type = "patches", revision = 42, baseRevision = 39, patches = Array.Empty<object>() });
    Check(replay.ReadUsage(threadB) is null, "revision gap invalidates usage until a new authoritative snapshot");

    // Exercise the IPC receiver, not just a preconstructed presentation string:
    // an ended turn must override an unfinished item still present in its history.
    foreach(var endState in new[]{"completed","interrupted","failed"})
    {
        Feed(threadB,new {type="snapshot",revision=20,conversationState=new {turns=new[]{new {turnId="ending-turn",turnStartedAtMs=2000,status="inProgress",items=Array.Empty<object>()}}}});
        Check(replay.Read(threadB).Activity=="working","IPC starts in Working before "+endState);
        Feed(threadB,new {type="patches",revision=21,baseRevision=20,patches=new[]{new {op="replace",path=new object[]{"turns",0,"status"},value=endState}}});
        Check(replay.Read(threadB).Activity=="ready"&&!replay.Read(threadB).ToolRunning,"IPC "+endState+" clears Working immediately");
        Feed(threadB,new {type="snapshot",revision=30,conversationState=new {turns=new[]{new {turnId="ending-tool",turnStartedAtMs=3000,status="inProgress",items=new[]{new {type="commandExecution",id="unfinished-tool",status="inProgress",startedAtMs=3000}}}}}});
        Check(replay.Read(threadB).Activity=="command","IPC command active before "+endState);
        Feed(threadB,new {type="patches",revision=31,baseRevision=30,patches=new[]{new {op="replace",path=new object[]{"turns",0,"status"},value=endState}}});
        Check(replay.Read(threadB).Activity=="ready"&&!replay.Read(threadB).ToolRunning,"IPC "+endState+" overrides an unfinished tool item");
    }


}

var commandDisplay=new LiveSpeedDisplay();commandDisplay.Read(reading,1000);
foreach(var activity in new[]{"working","thinking","command","searching","tool","ready"})
{
    var display=new LiveSpeedDisplay();display.Read(reading,1000);
    Check(display.Read(reading with{Activity=activity,LiveEstimateTps=1,LastOutputAtMs=1800},2000)==(60d,true),activity+" retains the last active rate without sampling filter decay");
    Check(display.Read(reading with{Activity="generating",LiveEstimateTps=80,LastOutputAtMs=3000},3000)==(80d,false),activity+" resumes on fresh text");
    Check(display.Read(reading with{Activity=activity,ThreadId="other",LastOutputAtMs=null},4000).Rate is null,activity+" cannot reuse another conversation's rate");
    Check(new LiveSpeedDisplay().Read(reading with{Activity=activity},1000).Rate is null,activity+" before first text does not invent a last rate");
}
Check(commandDisplay.Read(reading with{ToolRunning=true,LiveEstimateTps=1,LastOutputAtMs=1800},2000)==(60d,true),"running command holds the pre-command rate instead of a decayed 1 tok/s");
Check(commandDisplay.Read(reading with{ToolRunning=false,LiveEstimateTps=1,LastOutputAtMs=1800},5000)==(60d,true),"finishing a command does not publish a stale filter tail");
Check(commandDisplay.Read(reading with{ToolRunning=false,LiveEstimateTps=80,LastOutputAtMs=6000},6000)==(80d,false),"fresh visible output resumes live updates after the tool");
var firstDay=E("day-a",1000,500,100) with{Thread="a"};
var nextDay=E("day-b",2000,1000,200) with{Thread="b",At=firstDay.At.AddDays(1)};
var breakdownInput=new[]{firstDay,nextDay,firstDay};
var byThread=UsageBreakdown.ByThread(breakdownInput);var byDayThread=UsageBreakdown.ByDayAndThread(breakdownInput);
Check(byThread.Count==2&&byDayThread.Count==2,"cost leaderboard and daily conversation rows retain separate threads and dates");
Check(byThread.Sum(x=>x.Usage.Total)==UsageTotals.Sum(new[]{firstDay,nextDay}).Total,"breakdowns deduplicate response IDs rather than adding fork copies");
Check(byDayThread.Sum(x=>x.Usage.KnownApiUsd)==UsageTotals.Sum(new[]{firstDay,nextDay}).KnownApiUsd,"daily conversation costs sum to the total API equivalent");
Check(UsageBreakdown.ByThread(new[]{firstDay with{Model="unknown-new-model"}})[0].Usage.UnpricedResponses==1,"unknown model stays unpriced in the leaderboard");

var monthly=MonthlyApiArchive.Update(Array.Empty<MonthlyApiValue>(),new[]{firstDay,nextDay},new DateOnly(2026,11,15));
Check(monthly.Single().Month=="2026-11"&&!monthly.Single().Closed,"current UTC month is accumulated before close");
var closed=MonthlyApiArchive.Update(monthly,new[]{firstDay,nextDay},new DateOnly(2026,12,1));
Check(closed.Single().Closed,"previous month becomes a local closed summary");
var frozen=MonthlyApiArchive.Update(closed,new[]{firstDay with{Input=999999}},new DateOnly(2026,12,2));
Check(frozen.Single().KnownApiUsd==closed.Single().KnownApiUsd&&frozen.Single().Tokens==closed.Single().Tokens,"closed monthly summary is retained rather than rewritten by new prices or deleted history");
var monthPath=Path.Combine(Path.GetTempPath(),"codex-month-test-"+Guid.NewGuid().ToString("N")+".json");
try
{
    MonthlyApiArchive.Save(monthPath,new[]{firstDay,nextDay},new DateOnly(2026,11,15));
    var saved=File.ReadAllText(monthPath);MonthlyApiArchive.Save(monthPath,new[]{firstDay,nextDay},new DateOnly(2026,11,15));
    Check(saved==File.ReadAllText(monthPath),"unchanged monthly data does not produce different archive content");
}
finally{if(File.Exists(monthPath))File.Delete(monthPath);}

// 2026-11-10 audit regressions.
{
    string Stamp(long ms)=>DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");
    string Record(long at,string type,object payload)=>System.Text.Json.JsonSerializer.Serialize(new{timestamp=Stamp(at),type,payload});
    string Started(long at,string turn)=>Record(at,"event_msg",new{type="task_started",turn_id=turn});
    string Message(long at,string turn,long start)=>Record(at,"event_msg",new{type="item_completed",turn_id=turn,started_at_ms=start,completed_at_ms=at,item=new{type="AgentMessage"}});
    string Used(long at,string turn,string id,long tokens)=>Record(at,"token_usage_record",new{turn_id=turn,response_id=id,usage=new{output_tokens=tokens}});
    var late=new ResponseSpeedAnalyzer();
    foreach(var line in new[]{Started(1000,"A"),Message(3000,"A",2000),Used(3000,"A","r1",100),Started(4000,"B"),
        Used(4100,"A","late",5),Message(6000,"B",5000),Used(6000,"B","r3",100),Message(8000,"B",7000),Used(8000,"B","r4",100)})late.ObserveLine(line);
    Check(late.TurnId=="B"&&late.SessionTimedResponses==3&&late.SessionCompletedResponses==4&&late.SessionTimedTokens==300&&late.SessionTimedMilliseconds==3000,
        "a late usage record of an earlier turn stays untimed without unseating the current turn");

    var foreignHome=Path.Combine(Path.GetTempPath(),"codex-foreign-record-"+Guid.NewGuid().ToString("N"));
    try
    {
        var foreignSessions=Path.Combine(foreignHome,"sessions");Directory.CreateDirectory(foreignSessions);
        File.WriteAllText(Path.Combine(foreignSessions,"rollout-foreign.jsonl"),"\"token_count\"\n"+
            "{\"timestamp\":\""+Stamp(1)+"\",\"type\":\"token_usage_record\",\"payload\":\"token_usage_record\"}\n"+
            "{\"timestamp\":\""+Stamp(2)+"\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"ok\",\"thread_id\":\"t\",\"usage\":{\"input_tokens\":10,\"cached_input_tokens\":0,\"output_tokens\":5}}}\n");
        var foreignLedger=new UsageLedger();foreignLedger.Refresh(foreignSessions);
        Check(foreignLedger.Entries.Count==1&&foreignLedger.Entries[0].Output==5,"non-object usage records are skipped and later records still count");
    }
    finally{if(Directory.Exists(foreignHome))Directory.Delete(foreignHome,true);}

    var archived=new[]{new MonthlyApiValue("2026-09",1_000_000,12.5m,40,1,true)};
    var remaining=new UsageEntry("left","t",null,new DateTimeOffset(2026,9,5,0,0,0,TimeSpan.Zero),"gpt-6.1-sol",10,0,0,5,0);
    Check(MonthlyApiArchive.Update(archived,new[]{remaining},new DateOnly(2026,11,10)).Single()==archived[0],
        "a closed month with unpriced responses is not shrunk after its logs are deleted");
}
Console.WriteLine($"RESULT {checks} checks passed");
