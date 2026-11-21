namespace PhiEdit.App.Services.Audio;

/// <summary>预解码的 44.1kHz 立体声交错 PCM，音频回调中只读，不做解码。</summary>
public sealed class PcmAudioBuffer
{
    public const int SampleRate = 44100;

    public PcmAudioBuffer(float[] samples)
    {
        Samples = samples;
    }

    public float[] Samples { get; }

    public int Frames => Samples.Length / 2;

    public double DurationSeconds => (double)Frames / SampleRate;
}
