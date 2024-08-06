using Alsa.Net;
using Alsa.Net.Internal;
using System.IO;
using System.Threading;

namespace Example.Playback
{
    class Program
    {
        static void Main()
        {
            // create virtual interface to system default audio device
            System.Console.WriteLine("Started!");
            using ISoundDevice alsaDevice = AlsaDeviceBuilder.Create(new SoundDeviceSettings());
            System.Console.WriteLine("Device created!");
            // provide a wav stream 
            using var inputStream = new FileStream("/home/blackfox/Downloads/teszt.wav", FileMode.Open, FileAccess.Read, FileShare.Read);
            System.Console.WriteLine("File loaded!");

            // play on the device
            System.Console.WriteLine("Thread starting...");
            Thread player=new Thread(()=>PlayerThread(alsaDevice,inputStream));
            player.Start();
            System.Console.WriteLine("Thread done!");
            
            System.Console.ReadLine();
            alsaDevice.Pause();
            System.Console.WriteLine("Paused?");
            System.Console.ReadLine();
            alsaDevice.PlayFrom(3000);
            player.Join();
            System.Console.WriteLine("End");
        }
        static void PlayerThread(ISoundDevice device,Stream inputStream){
            System.Console.WriteLine("Play start...");
            device.Play(inputStream);
            System.Console.WriteLine("Play end, thread end!");
        }
    }
}
