﻿namespace Ciribob.DCS.SimpleRadio.Standalone.Common
{
    public class RadioInformation
    {
        public enum EncryptionMode
        {
            NO_ENCRYPTION = 0,
            ENCRYPTION_JUST_OVERLAY = 1,
            ENCRYPTION_FULL = 2,
            ENCRYPTION_COCKPIT_TOGGLE_OVERLAY_CODE = 3

            // 0  is no controls
            // 1 is FC3 Gui Toggle + Gui Enc key setting
            // 2 is InCockpit toggle + Incockpit Enc setting
            // 3 is Incockpit toggle + Gui Enc Key setting
        }

        public bool enc = false; // encrytion enabled
        public byte encKey = 0;
        public EncryptionMode encMode = EncryptionMode.NO_ENCRYPTION;

        public double freqMax = 1;
        public double freqMin = 1;
        public double frequency = 1;
        public byte modulation = 0;
        public string name = "";
        public double secondaryFrequency = 1;
        public float volume = 1.0f;

        /**
         * Used to determine if we should send an update to the server or not
         * We only need to do that if something that would stop us Receiving happens which
         * is frequencies and modulation
         */

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var compare = (RadioInformation) obj;

            if (!name.Equals(compare.name))
            {
                return false;
            }
            if (frequency != compare.frequency)
            {
                return false;
            }

            if (modulation != compare.modulation)
            {
                return false;
            }
            if (secondaryFrequency != compare.secondaryFrequency)
            {
                return false;
            }
            //if (volume != compare.volume)
            //{
            //    return false;
            //}
            //if (freqMin != compare.freqMin)
            //{
            //    return false;
            //}
            //if (freqMax != compare.freqMax)
            //{
            //    return false;
            //}


            return true;
        }
    }
}