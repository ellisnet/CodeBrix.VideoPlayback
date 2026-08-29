using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CodeBrix.VideoPlayback.Tests;

/// <summary>
/// Reads the <c>ffprobe</c> output recorded beside each golden asset, so the library's own reader can be
/// checked against a completely independent implementation rather than against itself.
/// </summary>
internal sealed class MatroskaOracle
{
    private MatroskaOracle(JsonDocument document)
    {
        Document = document;

        List<OracleStream> streams = new List<OracleStream>();
        if (document.RootElement.TryGetProperty("streams", out JsonElement streamArray))
        {
            foreach (JsonElement stream in streamArray.EnumerateArray())
            {
                streams.Add(new OracleStream(stream));
            }
        }

        Streams = streams;

        Dictionary<int, List<OracleFrame>> frames = new Dictionary<int, List<OracleFrame>>();
        if (document.RootElement.TryGetProperty("frames", out JsonElement frameArray))
        {
            foreach (JsonElement frame in frameArray.EnumerateArray())
            {
                OracleFrame parsed = new OracleFrame(frame);
                if (!frames.TryGetValue(parsed.StreamIndex, out List<OracleFrame> list))
                {
                    list = new List<OracleFrame>();
                    frames[parsed.StreamIndex] = list;
                }

                list.Add(parsed);
            }
        }

        Frames = frames;

        List<OracleChapter> chapters = new List<OracleChapter>();
        if (document.RootElement.TryGetProperty("chapters", out JsonElement chapterArray))
        {
            foreach (JsonElement chapter in chapterArray.EnumerateArray())
            {
                chapters.Add(new OracleChapter(chapter));
            }
        }

        Chapters = chapters;
    }

    public JsonDocument Document { get; }

    public IReadOnlyList<OracleStream> Streams { get; }

    public IReadOnlyDictionary<int, List<OracleFrame>> Frames { get; }

    public IReadOnlyList<OracleChapter> Chapters { get; }

    public static MatroskaOracle Load(string assetPath)
        => new MatroskaOracle(JsonDocument.Parse(File.ReadAllText(assetPath + ".probe.json")));

    public IReadOnlyList<OracleFrame> FramesFor(int streamIndex)
        => Frames.TryGetValue(streamIndex, out List<OracleFrame> list) ? list : Array.Empty<OracleFrame>();

    public OracleStream StreamOfType(string codecType)
    {
        foreach (OracleStream stream in Streams)
        {
            if (string.Equals(stream.CodecType, codecType, StringComparison.Ordinal)) return stream;
        }

        return null;
    }

    internal sealed class OracleStream
    {
        public OracleStream(JsonElement element)
        {
            Index = element.GetProperty("index").GetInt32();
            CodecType = Text(element, "codec_type");
            CodecName = Text(element, "codec_name");
            Width = Number(element, "width");
            Height = Number(element, "height");
            Channels = Number(element, "channels");
            SampleRate = ParseInt(Text(element, "sample_rate"));
            PixelFormat = Text(element, "pix_fmt");
            StartTime = ParseDouble(Text(element, "start_time"));

            if (element.TryGetProperty("tags", out JsonElement tags))
            {
                Language = Text(tags, "language");
                Title = Text(tags, "title");
            }

            if (element.TryGetProperty("disposition", out JsonElement disposition))
            {
                IsDefault = Number(disposition, "default") == 1;
                IsForced = Number(disposition, "forced") == 1;
            }
        }

        public int Index { get; }

        public string CodecType { get; }

        public string CodecName { get; }

        public int Width { get; }

        public int Height { get; }

        public int Channels { get; }

        public int SampleRate { get; }

        public string PixelFormat { get; }

        public double StartTime { get; }

        public string Language { get; } = string.Empty;

        public string Title { get; } = string.Empty;

        public bool IsDefault { get; }

        public bool IsForced { get; }
    }

    internal sealed class OracleFrame
    {
        public OracleFrame(JsonElement element)
        {
            // A subtitle frame carries none of stream_index, key_frame or pkt_size, so every field is
            // read defensively and a frame with no stream index is bucketed under -1 rather than dropped.
            StreamIndex = element.TryGetProperty("stream_index", out JsonElement index)
                && index.ValueKind == JsonValueKind.Number
                    ? index.GetInt32()
                    : -1;
            MediaType = Text(element, "media_type");
            IsKeyFrame = Number(element, "key_frame") == 1;
            PtsTime = ParseDouble(Text(element, "pts_time"));
            HasSize = element.TryGetProperty("pkt_size", out JsonElement size) && size.ValueKind == JsonValueKind.String;
            Size = ParseInt(Text(element, "pkt_size"));
        }

        public int StreamIndex { get; }

        public string MediaType { get; }

        public bool IsKeyFrame { get; }

        public double PtsTime { get; }

        public int Size { get; }

        public bool HasSize { get; }
    }

    internal sealed class OracleChapter
    {
        public OracleChapter(JsonElement element)
        {
            StartTime = ParseDouble(Text(element, "start_time"));
            EndTime = ParseDouble(Text(element, "end_time"));

            if (!element.TryGetProperty("tags", out JsonElement tags)) return;

            Title = Text(tags, "title");
            foreach (JsonProperty property in tags.EnumerateObject())
            {
                Tags[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        public double StartTime { get; }

        public double EndTime { get; }

        public string Title { get; } = string.Empty;

        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : string.Empty;

    private static int Number(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static int ParseInt(string text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static double ParseDouble(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0.0;
}
