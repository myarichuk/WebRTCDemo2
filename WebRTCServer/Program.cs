using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SIPSorcery;
using SIPSorcery.Media;
using SIPSorcery.Net;

namespace WebRTCAudioServer;

using SIPSorceryMedia.Abstractions;

public sealed class LivePcmAudioSource(IAudioEncoder encoder) : IAudioSource
{
    private AudioFormat _format = new (SDPWellKnownMediaFormatsEnum.G722);

    public bool IsAudioSourcePaused() => throw new NotImplementedException();

    public event EncodedSampleDelegate OnAudioSourceEncodedSample;
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
    public event RawAudioSampleDelegate OnAudioSourceRawSample; // optional; usually you raise encoded
    public event SourceErrorDelegate OnAudioSourceError;

    public Task StartAudio() => Task.CompletedTask;
    public Task CloseAudio() => Task.CompletedTask;
    public Task PauseAudio() => Task.CompletedTask;
    public Task ResumeAudio() => Task.CompletedTask;

    public List<AudioFormat> GetAudioSourceFormats() => [_format];
    public void SetAudioSourceFormat(AudioFormat fmt) => _format = fmt;

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
         // dont care
    }
    
    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) => 
        PushPcm16(sample, durationMilliseconds);
    
    public bool HasEncodedAudioSubscribers() => true;

    private void PushPcm16(short[] pcm16, uint durationMs = 20)
    {
        try
        {
            var encoded = encoder.EncodeAudio(pcm16, _format);
            OnAudioSourceEncodedSample?.Invoke(durationMs, encoded);
        }
        catch (Exception ex)
        {
            OnAudioSourceError?.Invoke($"Encode failed: {ex.Message}");
        }
    }
}


internal static class Program
{
    private const AudioSamplingRatesEnum StreamRate = AudioSamplingRatesEnum.Rate16KHz;

    private static RTCPeerConnection _pc;
    private static IAudioSource _audioSrc;

    private static async Task<int> Main()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        LogFactory.Set(loggerFactory);
            
        Console.WriteLine();
            
        var config = new RTCConfiguration
        {
            iceServers = []
        };
            
        _pc = new RTCPeerConnection(config);

        _audioSrc = new LivePcmAudioSource(new AudioEncoder());// new AudioExtrasSource(new AudioEncoder(false, true), new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });
        _audioSrc.OnAudioSourceEncodedSample += (fmt, buffer) => {
            _pc.SendAudio(fmt, buffer);
            Console.WriteLine($"tx {fmt.ToString()}, {buffer.Length} bytes");
        };
            
        var fmts = _audioSrc.GetAudioSourceFormats().ToList();
        var g722 = fmts.First(f => f.Codec == AudioCodecsEnum.G722);

        var audioTrack = new MediaStreamTrack([g722], MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(audioTrack);
            
        // routes samples via RPT -> SendAudio() is what pushes data to listeners
        _audioSrc.OnAudioSourceEncodedSample += ((units, sample) =>
        {
            Console.WriteLine($"Writing {units} -> {sample.Length} bytes");
            _pc.SendAudio(units, sample);
        });

        _pc.OnAudioFormatsNegotiated += formats =>
        {
            Console.WriteLine($"Negotiated audio formats: {string.Join(", ", formats)}");
            var chosen = formats.First(f => f.Codec == AudioCodecsEnum.G722);
            _audioSrc.SetAudioSourceFormat(chosen);
        };

        _pc.onconnectionstatechange += async state =>
        {
            Console.WriteLine($"[pc] state: {state}");
            if (state is RTCPeerConnectionState.failed or 
                RTCPeerConnectionState.closed or 
                RTCPeerConnectionState.disconnected)
            {
                await _audioSrc.CloseAudio();
            }
        };

        // 3) Offer/Answer (copy-paste signalling).
        var offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer);

        Console.WriteLine("\n=== OFFER (copy into your browser) ===");
        Console.WriteLine(JsonConvert.SerializeObject(offer, new StringEnumConverter()));
        Console.WriteLine("=== END OFFER ===\n");

        Console.WriteLine("Paste ANSWER JSON from the browser, then press Enter:");
        var answerJson = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(answerJson))
        {
            Console.WriteLine("No answer provided. Exiting.");
            return 1;
        }

        var answer = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(answerJson);
        var setRes = _pc.setRemoteDescription(answer);
        Console.WriteLine($"setRemoteDescription: {setRes}");

        var cts = new CancellationTokenSource();
        var genTask = Task.Run(() => SineGenerator(StreamRate, cts.Token));

        Console.WriteLine("Streaming. Press Enter to stop...");
        Console.ReadLine();
        await cts.CancelAsync();

        try
        {
            await genTask;
        }
        catch
        {
        }
        _pc.Close("done");
        return 0;
    }



    private static void SineGenerator(AudioSamplingRatesEnum rate, CancellationToken ct)
    {
        var sampleRate = (uint)rate;                  // 8000 or 16000
        var samplesPerFrame = sampleRate / 50;       // 20 ms
        double phase = 0;
        var twoPi = Math.PI * 2;
        var freq = 440.0; // A4
        var frame = new short[samplesPerFrame];

        while (!ct.IsCancellationRequested)
        {
            for (var i = 0; i < samplesPerFrame; i++)
            {
                var s = Math.Sin(phase) * 0.2; // amplitude
                frame[i] = (short)(s * short.MaxValue);
                phase += twoPi * freq / sampleRate;
                if (phase > twoPi) phase -= twoPi;
            }
            // var bytes = new byte[frame.Length * BytesPerSample];
            // Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);
            // push(bytes);
            _audioSrc.ExternalAudioSourceRawSample(AudioSamplingRatesEnum.Rate16KHz, sampleRate, frame);
            Thread.Sleep(20);
        }
    }
}

/// <summary>
/// A tiny blocking producer/consumer stream.
/// We write PCM chunks into it; AudioExtrasSource.SendAudioFromStream reads them.
/// </summary>
public sealed class ProducerConsumerStream : Stream
{
    private readonly BlockingCollection<byte[]> _q = new(new ConcurrentQueue<byte[]>());
    private byte[] _current = [];
    private int _pos;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }

    public void Complete() => _q.CompleteAdding();

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_pos < _current.Length)
            {
                var toCopy = Math.Min(count, _current.Length - _pos);
                Buffer.BlockCopy(_current, _pos, buffer, offset, toCopy);
                _pos += toCopy;
                return toCopy;
            }

            if (!_q.TryTake(out _current, Timeout.Infinite))
                return 0; // completed
            _pos = 0;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var chunk = new byte[count];
        Buffer.BlockCopy(buffer, offset, chunk, 0, count);
        _q.Add(chunk);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}