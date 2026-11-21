using NAudio.Vorbis;
using NAudio.Wave;

namespace PhiEdit.App.Services.Audio;

/// <summary>把 PEZ 内的 mp3/wav/ogg 一次性解码为统一 PCM，避免播放回调里读盘或解码。</summary>
public static class AudioDecoder
{
    private const int MaxMinutes = 20;

    public static PcmAudioBuffer Decode(byte[] bytes, string name)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using WaveStream reader = name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(stream)
            : name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                ? new WaveFileReader(stream)
                : new Mp3FileReader(stream);

        var sourceRate = reader.WaveFormat.SampleRate;
        var channels = Math.Max(1, Math.Min(2, reader.WaveFormat.Channels));
        if (sourceRate <= 0 || reader.Length / Math.Max(1.0, reader.WaveFormat.AverageBytesPerSecond) > MaxMinutes * 60)
        {
            throw new InvalidDataException($"音频 {name} 超出可解码范围。");
        }

        var provider = reader.ToSampleProvider();
        var source = new List<float>((int)Math.Min(reader.Length / 4, 64L * 1024 * 1024));
        var chunk = new float[8192 * channels];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                source.Add(chunk[index]);
            }
        }

        if (source.Count == 0)
        {
            throw new InvalidDataException($"音频 {name} 没有采样数据。");
        }

        var targetFrames = (int)Math.Ceiling(source.Count / (double)channels * PcmAudioBuffer.SampleRate / sourceRate);
        var output = new float[targetFrames * 2];
        for (var frame = 0; frame < targetFrames; frame++)
        {
            var position = frame * (double)sourceRate / PcmAudioBuffer.SampleRate;
            var index = (int)Math.Floor(position);
            var fraction = (float)(position - index);
            var next = Math.Min(index + 1, source.Count / channels - 1);
            for (var channel = 0; channel < 2; channel++)
            {
                var sourceChannel = channels == 1 ? 0 : channel;
                var left = source[Math.Min(index, next) * channels + sourceChannel];
                var right = source[next * channels + sourceChannel];
                output[frame * 2 + channel] = left + (right - left) * fraction;
            }
        }

        return new PcmAudioBuffer(output);
    }
}
