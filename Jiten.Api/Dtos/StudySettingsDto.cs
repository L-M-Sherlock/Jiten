using System.Text.Json.Serialization;

namespace Jiten.Api.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter<StudyInterleaving>))]
public enum StudyInterleaving
{
    Mixed,
    NewFirst,
    ReviewsFirst
}

[JsonConverter(typeof(JsonStringEnumConverter<StudyNewCardOrder>))]
public enum StudyNewCardOrder
{
    DeckFrequency,
    GlobalFrequency,
    Random
}

[JsonConverter(typeof(JsonStringEnumConverter<StudyReviewFrom>))]
public enum StudyReviewFrom
{
    AllTracked,
    StudyDecksOnly
}

[JsonConverter(typeof(JsonStringEnumConverter<ExampleSentencePosition>))]
public enum ExampleSentencePosition
{
    Hidden,
    Back,
    Front
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class StudySettingsDto
{
    [JsonPropertyName("newCardsPerDay")]
    public int NewCardsPerDay { get; set; } = 20;

    [JsonPropertyName("maxReviewsPerDay")]
    public int MaxReviewsPerDay { get; set; } = 200;

    [JsonPropertyName("gradingButtons")]
    public int GradingButtons { get; set; } = 4;

    [JsonPropertyName("interleaving")]
    public StudyInterleaving Interleaving { get; set; } = StudyInterleaving.Mixed;

    [JsonPropertyName("newCardOrder")]
    public StudyNewCardOrder NewCardOrder { get; set; } = StudyNewCardOrder.DeckFrequency;

    [JsonPropertyName("reviewFrom")]
    public StudyReviewFrom ReviewFrom { get; set; } = StudyReviewFrom.AllTracked;

    [JsonPropertyName("showPitchAccent")]
    public bool ShowPitchAccent { get; set; } = true;

    [JsonPropertyName("exampleSentencePosition")]
    public ExampleSentencePosition ExampleSentencePosition { get; set; } = ExampleSentencePosition.Back;

    [JsonPropertyName("showFrequencyRank")]
    public bool ShowFrequencyRank { get; set; } = true;

    [JsonPropertyName("showKanjiBreakdown")]
    public bool ShowKanjiBreakdown { get; set; } = true;

    [JsonPropertyName("showNextInterval")]
    public bool ShowNextInterval { get; set; }

    [JsonPropertyName("showKeybinds")]
    public bool ShowKeybinds { get; set; } = true;

    [JsonPropertyName("showElapsedTime")]
    public bool ShowElapsedTime { get; set; } = true;
}
