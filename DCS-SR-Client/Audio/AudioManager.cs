﻿using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Input;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Client.UI;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using FragLabs.Audio.Codecs;
using FragLabs.Audio.Codecs.Opus;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client
{
    public class AudioManager
    {
        public static readonly int SAMPLE_RATE = 16000;
        public static readonly int SEGMENT_FRAMES = 640; //480 for 8000 960 for 24000
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ConcurrentDictionary<string, SRClient> _clientsList;
        private int _bytesPerSegment;

        private readonly CachedAudioEffect[] _cachedAudioEffects;

        //    private readonly ConcurrentDictionary<string, ClientAudioProvider> _clientsBufferedAudio =
        //      new ConcurrentDictionary<string, ClientAudioProvider>();

        private OpusDecoder _decoder;
        private OpusEncoder _encoder;

        private byte[] _micClickOffBytes;
        private MixingSampleProvider _mixing;

        private byte[] _notEncodedBuffer = new byte[0];
        private BufferedWaveProvider _playBuffer;

        //main radio buffer
        private RadioAudioProvider[] _radioOutputBuffer;

        //secondary buffer for effects
        //plays in parallel with radio output buffer
        private RadioAudioProvider[] _effectsOutputBuffer;

        private int _segmentFrames;

        private float _speakerBoost = 1.0f;
        private volatile bool _stop = true;
        private UdpVoiceHandler _udpVoiceHandler;
        private VolumeSampleProvider _volumeSampleProvider;

        private WaveIn _waveIn;
        private WaveOut _waveOut;

        public AudioManager(ConcurrentDictionary<string, SRClient> clientsList)
        {
            _clientsList = clientsList;

            _cachedAudioEffects =
                new CachedAudioEffect[Enum.GetNames(typeof(CachedAudioEffect.AudioEffectTypes)).Length];
            for (var i = 0; i < _cachedAudioEffects.Length; i++)
            {
                _cachedAudioEffects[i] = new CachedAudioEffect((CachedAudioEffect.AudioEffectTypes) i);
            }
        }

        public float MicBoost { get; set; } = 1.0f;

        public float SpeakerBoost
        {
            get { return _speakerBoost; }
            set
            {
                _speakerBoost = value;
                if (_volumeSampleProvider != null)
                {
                    _volumeSampleProvider.Volume = value;
                }
            }
        }

        public bool Resample { get; set; }

        public void AddRadioAudio(byte[] radioPCMAudio, int radioId)
        {
            var radioOutput = _radioOutputBuffer[radioId];

            radioOutput.VolumeSampleProvider.Volume = RadioDCSSyncServer.DcsPlayerRadioInfo.radios[radioId].volume;

            _radioOutputBuffer[radioId].AddAudioSamples(radioPCMAudio, radioId);
        }

        public void StartEncoding(int mic, int speakers, string guid, InputDeviceManager inputManager, IPAddress ipAddress, int port)
        {
            _stop = false;

            try
            {
                InitMixer();

                InitAudioBuffers();

                //Audio manager should start / stop and cleanup based on connection successfull and disconnect
                //Should use listeners to synchronise all the state

                _waveOut = new WaveOut
                {
                    DesiredLatency = 100, //100ms latency in output buffer
                    DeviceNumber = speakers
                };

                _volumeSampleProvider = new VolumeSampleProvider(_mixing);

                _volumeSampleProvider.Volume = SpeakerBoost;

                if (Resample)
                {
                    var resampler = new WdlResamplingSampleProvider(_volumeSampleProvider, 44100); //resample and output at 44100
                    _waveOut.Init(resampler);
                }
                else
                {
                    _waveOut.Init(_volumeSampleProvider);
                }

                _waveOut.Init(_volumeSampleProvider);
                _waveOut.Play();

                _segmentFrames = SEGMENT_FRAMES; //160 frames is 20 ms of audio
                _encoder = OpusEncoder.Create(SAMPLE_RATE, 1, Application.Restricted_LowLatency);

                _decoder = OpusDecoder.Create(SAMPLE_RATE, 1);
                _bytesPerSegment = _encoder.FrameByteCount(_segmentFrames);

                _waveIn = new WaveIn(WaveCallbackInfo.FunctionCallback())
                {
                    BufferMilliseconds = 80,
                    DeviceNumber = mic
                };

                _waveIn.DataAvailable += _waveIn_DataAvailable;
                _waveIn.WaveFormat = new WaveFormat(SAMPLE_RATE, 16, 1); //take in at 8000

                _udpVoiceHandler = new UdpVoiceHandler(_clientsList, guid, ipAddress,port, _decoder, this, inputManager);
                var voiceSenderThread = new Thread(_udpVoiceHandler.Listen);

                voiceSenderThread.Start();

                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error starting audio Quitting! Error:" + ex.Message);

                Environment.Exit(1);
            }
        }

        private void InitMixer()
        {
            _mixing = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SAMPLE_RATE, 2));
            _mixing.ReadFully = true;
        }

        private void InitAudioBuffers()
        {
            _radioOutputBuffer = new RadioAudioProvider[RadioDCSSyncServer.DcsPlayerRadioInfo.radios.Length];
            _effectsOutputBuffer = new RadioAudioProvider[RadioDCSSyncServer.DcsPlayerRadioInfo.radios.Length];

            for (var i = 0; i < RadioDCSSyncServer.DcsPlayerRadioInfo.radios.Length; i++)
            {
                _radioOutputBuffer[i] = new RadioAudioProvider();
                _mixing.AddMixerInput(_radioOutputBuffer[i].VolumeSampleProvider);

                _effectsOutputBuffer[i] = new RadioAudioProvider();
                _mixing.AddMixerInput(_effectsOutputBuffer[i].VolumeSampleProvider);
            }
        }


        public void PlaySoundEffectStartReceive(int transmitOnRadio, bool encrypted, float volume)
        {
            var radioEffects = Settings.Instance.UserSettings[(int) SettingType.RadioClickEffects];
            if (radioEffects == "ON")
            {
                var _effectsBuffer = _effectsOutputBuffer[transmitOnRadio];

                var encyptionEffects = Settings.Instance.UserSettings[(int) SettingType.RadioEncryptionEffects];
                if (encrypted && encyptionEffects == "ON")
                {
                    _effectsBuffer.VolumeSampleProvider.Volume = volume;
                    _effectsBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.KY_58_RX].AudioEffectBytes,
                        transmitOnRadio);
                }
                else
                {
                    _effectsBuffer.VolumeSampleProvider.Volume = volume;
                    _effectsBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_TX].AudioEffectBytes,
                        transmitOnRadio);
                }
            }
        }

        public void PlaySoundEffectStartTransmit(int transmitOnRadio, bool encrypted, float volume)
        {
            var radioEffects = Settings.Instance.UserSettings[(int) SettingType.RadioClickEffectsTx];
            if (radioEffects == "ON")
            {
                var _effectBuffer = _effectsOutputBuffer[transmitOnRadio];

                var encyptionEffects = Settings.Instance.UserSettings[(int) SettingType.RadioEncryptionEffects];

                if (encrypted && encyptionEffects == "ON")
                {
                    _effectBuffer.VolumeSampleProvider.Volume = volume;
                    _effectBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.KY_58_TX].AudioEffectBytes,
                        transmitOnRadio);
                }
                else
                {
                    _effectBuffer.VolumeSampleProvider.Volume = volume;
                    _effectBuffer.AddAudioSamples(
                        _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_TX].AudioEffectBytes,
                        transmitOnRadio);
                }
            }
        }


        public void PlaySoundEffectEndReceive(int transmitOnRadio, float volume)
        {
            var radioEffects = Settings.Instance.UserSettings[(int) SettingType.RadioClickEffects];
            if (radioEffects == "ON")
            {
                var _effectsBuffer = _effectsOutputBuffer[transmitOnRadio];

                _effectsBuffer.VolumeSampleProvider.Volume = volume;
                _effectsBuffer.AddAudioSamples(
                    _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_RX].AudioEffectBytes,
                    transmitOnRadio);
            }
        }

        public void PlaySoundEffectEndTransmit(int transmitOnRadio, float volume)
        {
            var radioEffects = Settings.Instance.UserSettings[(int) SettingType.RadioClickEffectsTx];
            if (radioEffects == "ON")
            {
                var _effectBuffer = _effectsOutputBuffer[transmitOnRadio];

                _effectBuffer.VolumeSampleProvider.Volume = volume;
                _effectBuffer.AddAudioSamples(
                    _cachedAudioEffects[(int) CachedAudioEffect.AudioEffectTypes.RADIO_RX].AudioEffectBytes,
                    transmitOnRadio);
            }
        }


        private void _waveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            var soundBuffer = new byte[e.BytesRecorded + _notEncodedBuffer.Length];

            for (var i = 0; i < _notEncodedBuffer.Length; i++)
                soundBuffer[i] = _notEncodedBuffer[i];

            for (var i = 0; i < e.BytesRecorded; i++)
                soundBuffer[i + _notEncodedBuffer.Length] = e.Buffer[i];

            var byteCap = _bytesPerSegment;
            //      Console.WriteLine("{0} ByteCao", byteCap);
            var segmentCount = (int) Math.Floor((decimal) soundBuffer.Length/byteCap);
            var segmentsEnd = segmentCount*byteCap;
            var notEncodedCount = soundBuffer.Length - segmentsEnd;
            _notEncodedBuffer = new byte[notEncodedCount];
            for (var i = 0; i < notEncodedCount; i++)
            {
                _notEncodedBuffer[i] = soundBuffer[segmentsEnd + i];
            }

            for (var i = 0; i < segmentCount; i++)
            {
                //create segment of audio
                var segment = new byte[byteCap];
                for (var j = 0; j < segment.Length; j++)
                {
                    segment[j] = soundBuffer[i*byteCap + j];
                }

                //boost microphone volume if needed
                if (MicBoost != 1.0f)
                {
                    for (var n = 0; n < segment.Length; n += 2)
                    {
                        var sample = (short) ((segment[n + 1] << 8) | segment[n + 0]);
                        // n.b. no clipping test going on here // FROM NAUDIO SOURCE !
                        sample = (short) (sample*MicBoost);
                        segment[n] = (byte) (sample & 0xFF);
                        segment[n + 1] = (byte) (sample >> 8);
                    }
                }

                //encode as opus bytes
                int len;
                var buff = _encoder.Encode(segment, segment.Length, out len);

                if (_udpVoiceHandler != null && buff != null && len > 0)
                    _udpVoiceHandler.Send(buff, len);
            }
        }

        public void StopEncoding()
        {
            if (_mixing != null)
            {
                _radioOutputBuffer = null;
                _effectsOutputBuffer = null;

                _volumeSampleProvider = null;
                _mixing.RemoveAllMixerInputs();
                _mixing = null;
            }

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
            }

            if (_playBuffer != null)
            {
                _playBuffer.ClearBuffer();
                _playBuffer = null;
            }


            if (_encoder != null)
            {
                _encoder.Dispose();
                _encoder = null;
            }

            if (_decoder != null)
            {
                _decoder.Dispose();
                _decoder = null;
            }
            if (_udpVoiceHandler != null)
            {
                _udpVoiceHandler.RequestStop();
                _udpVoiceHandler = null;
            }

            _stop = true;
        }
    }
}