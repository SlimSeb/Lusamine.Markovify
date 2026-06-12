using Lusamine.Markovify;

// A small public-domain-ish corpus to train on. Swap this for a file of your own:
//   var corpus = File.ReadAllText(args[0]);
const string corpus = """
    The sun rose over the quiet hills and the river sparkled in the morning light.
    The river ran past the quiet village and the children laughed in the morning air.
    The old farmer walked along the winding road and carried a basket of ripe apples.
    The young farmer walked across the open field and carried a basket of fresh bread.
    The travelers rested beneath the shady oak tree and watched the clouds drift past the hills.
    The travelers crossed the winding road and watched the river sparkle in the evening light.
    A gentle breeze moved through the tall grass and the birds began to sing in the trees.
    A gentle breeze moved across the open field and the children began to run through the grass.
    By noon the sun was high over the hills and the village settled into a quiet rest.
    By evening the stars filled the sky and the travelers settled into a peaceful sleep.
    The dog barked at the drifting clouds and ran across the open field toward the river.
    The dog rested beneath the oak tree and watched the children run through the tall grass.
    """;

Console.WriteLine("=== Lusamine.Markovify sample ===\n");

// Use a fixed seed so the sample output is reproducible.
var rng = new Random(2026);
var model = new Text(corpus, stateSize: 2, rng: rng);

Console.WriteLine("-- Five generated sentences --");
for (var i = 0; i < 5; i++)
{
    var sentence = model.MakeSentence();
    Console.WriteLine($"  {i + 1}. {sentence ?? "(no sentence found)"}");
}

Console.WriteLine("\n-- A short sentence (<= 80 chars) --");
Console.WriteLine($"  {model.MakeShortSentence(maxChars: 80, tries: 50) ?? "(none)"}");

Console.WriteLine("\n-- A sentence starting with \"The\" --");
Console.WriteLine($"  {model.MakeSentenceWithStart("The", strict: false) ?? "(none)"}");

// Persist and reload the trained model. ToJson/FromJson go through a single
// string, which is handy for small models; for huge corpora use Save/Load or
// WriteTo (shown below), which stream and avoid .NET's ~1 GB string limit.
Console.WriteLine("\n-- Round-tripping the model through JSON --");
var json = model.ToJson();
Console.WriteLine($"  serialized {json.Length} characters of model data");
var reloaded = Text.FromJson(json, new Random(7));
Console.WriteLine($"  reloaded -> {reloaded.MakeSentence(testOutput: false)}");

// Save the trained model to a file and load it back later.
Console.WriteLine("\n-- Saving and loading the model from a file --");
var modelPath = Path.Combine(Path.GetTempPath(), "lusamine-sample-model.json");
model.Save(modelPath);
Console.WriteLine($"  saved model to {modelPath}");
var loaded = Text.Load(modelPath, new Random(7));
Console.WriteLine($"  loaded -> {loaded.MakeSentence(testOutput: false)}");

// Combine two models into one.
Console.WriteLine("\n-- Combining two models --");
var weather = new Text("The storm rolled in fast. Thunder shook the windows all night.", rng: new Random(1));
var combined = Text.Combine([model, weather], [1.0, 1.5], new Random(3));
Console.WriteLine($"  {combined.MakeSentence(testOutput: false)}");

// Train from a streamed sequence of tokenized sentences (for corpora too large
// to hold as one string). Here the lines stand in for File.ReadLines(path).
Console.WriteLine("\n-- Streaming a corpus through FromSentences --");
var streamed = corpus
    .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => Splitters.SplitIntoWords(s));
var streamModel = Text.FromSentences(streamed, stateSize: 2, rng: new Random(5));
Console.WriteLine($"  {streamModel.MakeSentence(testOutput: false)}");

Console.WriteLine("\n-- NewlineText (each line is its own sentence) --");
const string lines = """
    roses are red
    violets are blue
    code that compiles
    is a dream come true
    """;
var poem = new NewlineText(lines, stateSize: 1, rng: new Random(4));
Console.WriteLine($"  {poem.MakeSentence(testOutput: false)}");
