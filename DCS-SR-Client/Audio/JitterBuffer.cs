﻿using System;
using System.Collections.Generic;
using System.Linq;
using Ciribob.DCS.SimpleRadio.Standalone.Client.UI;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using NAudio.Dsp;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Audio
{
    public class JitterBuffer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Dictionary<string, List<ClientAudio>>[] _clientRadioBuffers = new Dictionary<string, List<
            ClientAudio>>[4];

        private readonly object _lock = new object();

        private readonly Random _random = new Random();
        private readonly Settings _settings;

        private bool[] _hasDecryptedAudio = new bool[4];
        private BiQuadFilter _highPassFilter;
        private BiQuadFilter _lowPassFilter;

        public JitterBuffer()
        {
            for (var i = 0; i < _clientRadioBuffers.Length; i++)
            {
                _clientRadioBuffers[i] = new Dictionary<string, List<ClientAudio>>();
            }

            _settings = Settings.Instance;

            _highPassFilter = BiQuadFilter.HighPassFilter(AudioManager.SAMPLE_RATE, 520, 0.97f);
            _lowPassFilter = BiQuadFilter.LowPassFilter(AudioManager.SAMPLE_RATE, 4130, 2.0f);
        }

        public void AddAudio(ClientAudio audio)
        {
            lock (_lock)
            {
                var radioBuffer = _clientRadioBuffers[audio.ReceivedRadio];

                if (!radioBuffer.ContainsKey(audio.ClientGuid))
                {
                    radioBuffer[audio.ClientGuid] = new List<ClientAudio> {audio};
                }
                else
                {
                    //   logger.Info("adding");
                    radioBuffer[audio.ClientGuid].Add(audio);
                }
            }
        }


        public RadioMixDown[] MixDown()
        {
            // long start = Environment.TickCount;
            lock (_lock)
            {
                var radioMix = new RadioMixDown[_clientRadioBuffers.Length];

                bool _addEffect = _settings.UserSettings[(int) SettingType.RadioEffects] == "ON";


                

                for (var i = 0; i < _clientRadioBuffers.Length; i++)
                {

                    var radioBytes = ConversionHelpers.ShortArrayToByteArray(
                        
                            AddRadioEffect(RadioMixDown(_clientRadioBuffers[i], i),_addEffect)
                        );

                    radioMix[i] = new RadioMixDown
                    {
                        RadioAudioPCM = (radioBytes),
                        HasDecryptedAudio = _hasDecryptedAudio[i]
                        //mark if this contains encrypted audio for effects
                    };
                }

                Clear();

                // Logger.Debug("Mixdown took: "+(Environment.TickCount - start));

                return radioMix;
            }
        }

        private short[] AddRadioEffect(short[] mixedAudio ,bool effect)
        {
            if (!effect)
            {
                return mixedAudio;
            }

            for (int i = 0; i < mixedAudio.Length; i++)
            {

                float audio = mixedAudio[i] / 32768f;

                audio = _highPassFilter.Transform(audio);

                if (float.IsNaN(audio))
                    audio = _lowPassFilter.Transform(mixedAudio[i]);
                else
                    audio = _lowPassFilter.Transform(audio);

                if (!float.IsNaN(audio))
                {
              
                    // clip
                    if (audio > 1.0f)
                        audio = 1.0f;
                    if (audio < -1.0f)
                        audio = -1.0f;

                    mixedAudio[i] = (short)(audio * 32767);
                }

            }
            return mixedAudio;
            
        }

        private short[] RadioMixDown(Dictionary<string, List<ClientAudio>> radioData, int radioId)
        {
            //now process all the clientAudio and add to the final mixdown

            //init mixdown with the longest audio from a client
            // this means the mixing works properly rather than mixing
            //with silence initially which will cause issues
            var mixDown = InitRadioMixDown(radioData, radioId);

            foreach (var clientAudioList in radioData.Values)
            {
                //perclient audio
                foreach (var clientAudio in clientAudioList)
                {
                    var clientAudioBytesArray = clientAudio.PcmAudioShort;
                    var decrytable = clientAudio.Decryptable || clientAudio.Encryption == 0;

                    //mark that we have decrpyted encrypted audio for sound effects
                    if (decrytable && clientAudio.Encryption > 0)
                    {
                        _hasDecryptedAudio[radioId] = true;
                    }

                    for (var i = 0; i < clientAudioBytesArray.Count(); i++)
                    {
                        short speaker1Short = 0;
                        if (decrytable)
                        {
                            speaker1Short = clientAudioBytesArray[i];

                            if (clientAudio.RecevingPower < 0)
                            {
                                //calculate % loss - not real percent coz is logs
                                var loss = clientAudio.RecevingPower/RadioCalculator.RXSensivity;

                                //add in radio loss
                                //if more than 0.6 loss reduce volume
                                if (loss > 0.6)
                                {
                                    speaker1Short = (short) (speaker1Short*(1.0f - loss));
                                }
                            }

                            //0 is no loss so if more than 0 reduce volume
                            if (clientAudio.LineOfSightLoss > 0)
                            {
                                speaker1Short = (short) (speaker1Short*(1.0f - clientAudio.LineOfSightLoss));
                            }
                        }
                        else
                        {
                            speaker1Short = RandomShort();
                        }

                        var speaker2Short = mixDown[i];


                        mixDown[i] = MixSpeakers(speaker1Short, speaker2Short);
                    }
                }
            }


            return mixDown;
        }

        private short[] InitRadioMixDown(Dictionary<string, List<ClientAudio>> radioData, int radioId)
        {
            string longestGuid = null;
            var longestCount = 0;


            foreach (var clientAudioList in radioData.Values)
            {
                //perclient audio
                var clientCount = 0;
                string clientGuid = null;
                foreach (var clientAudio in clientAudioList)
                {
                    clientCount += clientAudio.PcmAudioShort.Length;

                    if (clientGuid == null)
                        clientGuid = clientAudio.ClientGuid;
                }

                if (clientCount > longestCount)
                {
                    longestCount = clientCount;
                    longestGuid = clientGuid;
                }
            }

            if (longestGuid != null)
            {
                var clientAudio = radioData[longestGuid];

                var mixDownInit = new List<short>();

                var decryptable = true;
                foreach (var audio in clientAudio)
                {
                    decryptable = audio.Decryptable || audio.Encryption == 0;

                    if (decryptable && audio.Encryption > 0)
                    {
                        _hasDecryptedAudio[radioId] = true;
                    }

                    mixDownInit.AddRange(audio.PcmAudioShort);
                }

                //remove now we've processed it
                radioData.Remove(longestGuid);

                var initArray = mixDownInit.ToArray();

                //now randomise if not decrytable
                if (!decryptable)
                {
                    for (var i = 0; i < initArray.Length; i++)
                    {
                        initArray[i] = RandomShort();
                    }
                }

                return initArray;
            }
            return new short[0];
        }


        private short MixSpeakers(int speaker1, int speaker2)
        {
            //method 1
            //  return (short) (speaker1 + speaker2 - speaker1 * speaker2 / 65535);

            //method 2
            //int tmp = speaker1 + speaker2;
            //return (short)(tmp / 2);


            //method 3
            //float samplef1 = speaker1 / 32768.0f;
            //float samplef2 = speaker2 / 32768.0f;
            //float mixed = samplef1 + samplef2;
            //// reduce the volume a bit:
            //mixed *= 0.8f;
            //// hard clipping
            //if (mixed > 1.0f) mixed = 1.0f;
            //if (mixed < -1.0f) mixed = -1.0f;
            //short outputSample = (short)(mixed * 32768.0f);
            //return outputSample;

            //method 4 Vicktor Toth - http://www.vttoth.com/CMS/index.php/technical-notes/68
            int m; // mixed result will go here

            // Make both samples unsigned (0..65535)
            speaker1 += 32768;
            speaker2 += 32768;

            // Pick the equation
            if ((speaker1 < 32768) || (speaker2 < 32768))
            {
                // Viktor's first equation when both sources are "quiet"
                // (i.e. less than middle of the dynamic range)
                m = speaker1*speaker2/32768;
            }
            else
            {
                // Viktor's second equation when one or both sources are loud
                m = 2*(speaker1 + speaker2) - speaker1*speaker2/32768 - 65536;
            }

            // Output is unsigned (0..65536) so convert back to signed (-32768..32767)
            if (m == 65536)
            {
                m = 65535;
            }

            m -= 32768;

            return (short) m;
        }

        private short RandomShort()
        {
            //random short at max volume at eights
            return (short) _random.Next(-32768/8, 32768/8);
        }

        public static byte[] CreateLeftMix(byte[] pcmAudio)
        {
            var stereoMix = new byte[pcmAudio.Length*2];
            for (var i = 0; i < pcmAudio.Length/2; i++)
            {
                stereoMix[i*4] = pcmAudio[i*2];
                stereoMix[i*4 + 1] = pcmAudio[i*2 + 1];

                stereoMix[i*4 + 2] = 0;
                stereoMix[i*4 + 3] = 0;
            }
            return stereoMix;
        }

        public static byte[] CreateRightMix(byte[] pcmAudio)
        {
            var stereoMix = new byte[pcmAudio.Length*2];
            for (var i = 0; i < pcmAudio.Length/2; i++)
            {
                stereoMix[i*4] = 0;
                stereoMix[i*4 + 1] = 0;

                stereoMix[i*4 + 2] = pcmAudio[i*2];
                stereoMix[i*4 + 3] = pcmAudio[i*2 + 1];
            }
            return stereoMix;
        }

        public static byte[] CreateStereoMix(byte[] pcmAudio)
        {
            var stereoMix = new byte[pcmAudio.Length*2];
            for (var i = 0; i < pcmAudio.Length/2; i++)
            {
                stereoMix[i*4] = pcmAudio[i*2];
                stereoMix[i*4 + 1] = pcmAudio[i*2 + 1];

                stereoMix[i*4 + 2] = pcmAudio[i*2];
                stereoMix[i*4 + 3] = pcmAudio[i*2 + 1];
            }
            return stereoMix;
        }

        public void Clear()
        {
            //clear buffer
            lock (_lock)
            {
                _hasDecryptedAudio = new bool[4];

                foreach (var perRadio in _clientRadioBuffers)
                {
                    perRadio.Clear();
                }
            }
        }
    }
}