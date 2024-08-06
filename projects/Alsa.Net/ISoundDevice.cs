using System;
using System.IO;
using System.Threading;

namespace Alsa.Net
{
    /// <summary>
    /// virtual sound interface connected to configured hardware devices
    /// </summary>
    public interface ISoundDevice : IDisposable
    {
        /// <summary>
        /// sound device settings like playback-, mixer- or recording device
        /// </summary>
        SoundDeviceSettings Settings { get; }

        /// <summary>
        /// set or get the volume of the playback device.
        /// </summary>
        /// <remarks>ensure this is supported by your device</remarks>
        long PlaybackVolume { get; set; }

        /// <summary>
        /// mute / unmute the playback device or get the current state
        /// </summary>
        bool PlaybackMute { get; set; }

        /// <summary>
        /// play a wav file on the playback device
        /// </summary>
        /// <param name="wavPath">path to wav file</param>
        void Play(string wavPath);

        /// <summary>
        /// play a wav stream
        /// </summary>
        /// <param name="wavStream">stream of wav data to play</param>
        void Play(Stream wavStream);

        /// <summary>
        /// play a wav file on the playback device until end of file or cancellation
        /// </summary>
        /// <param name="wavPath">path to wav file</param>
        /// <param name="cancellationToken">token to stop playback</param>
        void Play(string wavPath, CancellationToken cancellationToken);

        /// <summary>
        /// Pauses the player
        /// </summary>
        void Pause();

        /// <summary>
        /// Resumes the player
        /// </summary>
        void Resume();

        /// <summary>
        /// Plays the player from a given point
        /// </summary>
        void PlayFrom(long ms);

        /// <summary>
        /// play a wav stream until end of stream oder cancellation
        /// </summary>
        /// <param name="wavStream">stream of wav data to play</param>
        /// /// <param name="cancellationToken">token to stop playback</param>
        void Play(Stream wavStream, CancellationToken cancellationToken);
    }
}
