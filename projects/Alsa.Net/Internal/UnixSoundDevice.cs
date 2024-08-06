using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;

namespace Alsa.Net.Internal
{
    class UnixSoundDevice : ISoundDevice
    {
        static readonly object PlaybackInitializationLock = new();
        static readonly object MixerInitializationLock = new();
        private readonly Stopwatch playTimeWatch=new Stopwatch();
        private long playTime=0;
        public long EllapsedMs{get=>GetElapsedMs();}

        private readonly ManualResetEvent PlayPause = new ManualResetEvent(true);

        private Stream wavStream;
        private uint byteRate;

        public SoundDeviceSettings Settings { get; }
        public long PlaybackVolume { get => GetPlaybackVolume(); set => SetPlaybackVolume(value); }
        public bool PlaybackMute { get => _playbackMute; set => SetPlaybackMute(value); }

        bool _playbackMute;
        IntPtr _playbackPcm;
        IntPtr _mixer;
        IntPtr _mixelElement;
        bool _wasDisposed;

        private long headerEnd=-1;
        pause_state paused = pause_state.PLAYING;

        public UnixSoundDevice(SoundDeviceSettings settings)
        {
            Settings = settings;
        }

        private long GetElapsedMs(){
            playTime+=playTimeWatch.ElapsedMilliseconds;
            if(paused==pause_state.PLAYING){
                playTimeWatch.Restart();
            }
            else{
                playTimeWatch.Reset();
            }
            return playTime;
        }

        public void Pause()
        {
            if (_wasDisposed)
                throw new ObjectDisposedException(nameof(UnixSoundDevice));
            playTimeWatch.Stop();
            paused = pause_state.JUSTPAUSED;
            PlayPause.Reset();
        }

        public void Resume(){
            PlayFrom(EllapsedMs);
        }

        public void Seek(long ms){
            playTime+=ms;
            if(paused==pause_state.PLAYING){
                PlayFrom(EllapsedMs);
            }
            else{
                long position=headerEnd+((long)Math.Floor(Convert.ToDouble(ms)/1000*byteRate));
                wavStream.Position=position>=0?position:headerEnd;
                InteropAlsa.snd_pcm_drop(_playbackPcm);
                InteropAlsa.snd_pcm_prepare(_playbackPcm);
            }
        }

        public void PlayFrom(long ms)
        {
            if (_wasDisposed)
                throw new ObjectDisposedException(nameof(UnixSoundDevice));
            long position=headerEnd+((long)Math.Floor(Convert.ToDouble(ms)/1000*byteRate));
            wavStream.Position=position>=0?position:headerEnd;
            InteropAlsa.snd_pcm_drop(_playbackPcm);
            InteropAlsa.snd_pcm_prepare(_playbackPcm);
            paused=pause_state.PLAYING;
            playTimeWatch.Start();
            PlayPause.Set();
        }

        public void Play(string wavPath)
        {
            if (_wasDisposed)
                throw new ObjectDisposedException(nameof(UnixSoundDevice));

            using var fs = File.Open(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Play(fs, CancellationToken.None);
        }

        public void Play(string wavPath, CancellationToken cancellationToken)
        {
            if (_wasDisposed)
                throw new ObjectDisposedException(nameof(UnixSoundDevice));

            using var fs = File.Open(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Play(fs, cancellationToken);
        }

        public void Play(Stream wavStream)
        {
            if (_wasDisposed)
                throw new ObjectDisposedException(nameof(UnixSoundDevice));

            Play(wavStream, CancellationToken.None);
        }

        public void Play(Stream wavStream, CancellationToken cancellationToken)
        {
            if (_wasDisposed)
                throw new ObjectDisposedException(nameof(UnixSoundDevice));
            this.wavStream=wavStream;
            var parameter = new IntPtr();
            var dir = 0;
            var header = WavHeader.FromStream(wavStream);
            byteRate=header.ByteRate;
            headerEnd=wavStream.Position;

            OpenPlaybackPcm();
            PcmInitialize(_playbackPcm, header, ref parameter, ref dir);
            WriteStream(wavStream, header, ref parameter, ref dir, cancellationToken);
            ClosePlaybackPcm();
        }

        unsafe void WriteStream(Stream wavStream, WavHeader header, ref IntPtr @params, ref int dir, CancellationToken cancellationToken)
        {
            ulong frames;

            fixed (int* dirP = &dir)
                ThrowErrorMessage(InteropAlsa.snd_pcm_hw_params_get_period_size(@params, &frames, dirP), ExceptionMessages.CanNotGetPeriodSize);

            var bufferSize = frames * header.BlockAlign;
            var readBuffer = new byte[(int)bufferSize];

            playTimeWatch.Start();

            fixed (byte* buffer = readBuffer)
            {
                //long parts=0;
                while (!_wasDisposed && !cancellationToken.IsCancellationRequested && wavStream.Read(readBuffer) != 0)
                {
                    // play/pause implementation goes here
                    //System.Console.WriteLine("Playing part "+parts++);

                    //System.Console.WriteLine(wavStream.Position);
                    PlayPause.WaitOne();
                    ThrowErrorMessage(InteropAlsa.snd_pcm_writei(_playbackPcm, (IntPtr)buffer, frames), ExceptionMessages.CanNotWriteToDevice);
                    if (paused==pause_state.JUSTPAUSED)
                    {
                        paused=pause_state.PAUSED;
                        InteropAlsa.snd_pcm_drop(_playbackPcm);
                    }
                }
            }
        }

        unsafe void PcmInitialize(IntPtr pcm, WavHeader header, ref IntPtr @params, ref int dir)
        {
            ThrowErrorMessage(InteropAlsa.snd_pcm_hw_params_malloc(ref @params), ExceptionMessages.CanNotAllocateParameters);
            ThrowErrorMessage(InteropAlsa.snd_pcm_hw_params_any(pcm, @params), ExceptionMessages.CanNotFillParameters);
            ThrowErrorMessage(InteropAlsa.snd_pcm_hw_params_set_access(pcm, @params, snd_pcm_access_t.SND_PCM_ACCESS_RW_INTERLEAVED), ExceptionMessages.CanNotSetAccessMode);

            var formatResult = (header.BitsPerSample / 8) switch
            {
                1 => InteropAlsa.snd_pcm_hw_params_set_format(pcm, @params, snd_pcm_format_t.SND_PCM_FORMAT_U8),
                2 => InteropAlsa.snd_pcm_hw_params_set_format(pcm, @params, snd_pcm_format_t.SND_PCM_FORMAT_S16_LE),
                3 => InteropAlsa.snd_pcm_hw_params_set_format(pcm, @params, snd_pcm_format_t.SND_PCM_FORMAT_S24_LE),
                _ => throw new AlsaDeviceException(ExceptionMessages.BitsPerSampleError)
            };
            ThrowErrorMessage(formatResult, ExceptionMessages.CanNotSetFormat);

            ThrowErrorMessage(InteropAlsa.snd_pcm_hw_params_set_channels(pcm, @params, header.NumChannels), ExceptionMessages.CanNotSetChannel);

            var val = header.SampleRate;
            fixed (int* dirP = &dir)
                ThrowErrorMessage(InteropAlsa.snd_pcm_hw_params_set_rate_near(pcm, @params, &val, dirP), ExceptionMessages.CanNotSetRate);

            ThrowErrorMessage(InteropAlsa.snd_pcm_hw_params(pcm, @params), ExceptionMessages.CanNotSetHwParams);
        }

        void SetPlaybackVolume(long volume)
        {
            OpenMixer();

            ThrowErrorMessage(InteropAlsa.snd_mixer_selem_set_playback_volume(_mixelElement, snd_mixer_selem_channel_id.SND_MIXER_SCHN_FRONT_LEFT, volume), ExceptionMessages.CanNotSetVolume);
            ThrowErrorMessage(InteropAlsa.snd_mixer_selem_set_playback_volume(_mixelElement, snd_mixer_selem_channel_id.SND_MIXER_SCHN_FRONT_RIGHT, volume), ExceptionMessages.CanNotSetVolume);

            CloseMixer();
        }

        unsafe long GetPlaybackVolume()
        {
            long volumeLeft;
            long volumeRight;

            OpenMixer();

            ThrowErrorMessage(InteropAlsa.snd_mixer_selem_get_playback_volume(_mixelElement, snd_mixer_selem_channel_id.SND_MIXER_SCHN_FRONT_LEFT, &volumeLeft), ExceptionMessages.CanNotSetVolume);
            ThrowErrorMessage(InteropAlsa.snd_mixer_selem_get_playback_volume(_mixelElement, snd_mixer_selem_channel_id.SND_MIXER_SCHN_FRONT_RIGHT, &volumeRight), ExceptionMessages.CanNotSetVolume);

            CloseMixer();

            return (volumeLeft + volumeRight) / 2;
        }

        void SetPlaybackMute(bool isMute)
        {
            _playbackMute = isMute;

            OpenMixer();

            ThrowErrorMessage(InteropAlsa.snd_mixer_selem_set_playback_switch_all(_mixelElement, isMute ? 0 : 1), ExceptionMessages.CanNotSetMute);

            CloseMixer();
        }

        void OpenPlaybackPcm()
        {
            if (_playbackPcm != default)
                return;

            lock (PlaybackInitializationLock)
                ThrowErrorMessage(InteropAlsa.snd_pcm_open(ref _playbackPcm, Settings.PlaybackDeviceName, snd_pcm_stream_t.SND_PCM_STREAM_PLAYBACK, 0), ExceptionMessages.CanNotOpenPlayback);
        }

        void ClosePlaybackPcm()
        {
            if (_playbackPcm == default)
                return;

            ThrowErrorMessage(InteropAlsa.snd_pcm_drain(_playbackPcm), ExceptionMessages.CanNotDropDevice);
            ThrowErrorMessage(InteropAlsa.snd_pcm_close(_playbackPcm), ExceptionMessages.CanNotCloseDevice);

            _playbackPcm = default;
        }

        void OpenMixer()
        {
            if (_mixer != default)
                return;

            lock (MixerInitializationLock)
            {
                ThrowErrorMessage(InteropAlsa.snd_mixer_open(ref _mixer, 0), ExceptionMessages.CanNotOpenMixer);
                ThrowErrorMessage(InteropAlsa.snd_mixer_attach(_mixer, Settings.MixerDeviceName), ExceptionMessages.CanNotAttachMixer);
                ThrowErrorMessage(InteropAlsa.snd_mixer_selem_register(_mixer, IntPtr.Zero, IntPtr.Zero), ExceptionMessages.CanNotRegisterMixer);
                ThrowErrorMessage(InteropAlsa.snd_mixer_load(_mixer), ExceptionMessages.CanNotLoadMixer);

                _mixelElement = InteropAlsa.snd_mixer_first_elem(_mixer);
            }
        }

        void CloseMixer()
        {
            if (_mixer == default)
                return;

            ThrowErrorMessage(InteropAlsa.snd_mixer_close(_mixer), ExceptionMessages.CanNotCloseMixer);

            _mixer = default;
            _mixelElement = default;
        }

        public void Dispose()
        {
            if (_wasDisposed)
                return;

            _wasDisposed = true;

            ClosePlaybackPcm();
            CloseMixer();
        }

        void ThrowErrorMessage(int errorNum, string message)
        {
            if (errorNum >= 0)
                return;

            var errorMsg = Marshal.PtrToStringAnsi(InteropAlsa.snd_strerror(errorNum));
            throw new AlsaDeviceException($"{message}. Error {errorNum}. {errorMsg}.");
        }
    }
}
