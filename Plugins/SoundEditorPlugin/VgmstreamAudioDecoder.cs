using NAudio.Wave;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SoundEditorPlugin
{
    internal static class VgmstreamAudioDecoder
    {
        private const int DecodeTimeoutMilliseconds = 120000;

        public static bool TryDecode(
            byte[] source,
            out short[] samples,
            out int sampleRate,
            out int channels,
            out string error)
        {
            samples = new short[0];
            sampleRate = 0;
            channels = 0;
            error = "";

            string decoder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "ThirdParty",
                "vgmstream",
                "vgmstream-cli.exe");
            if (!File.Exists(decoder))
            {
                error = "vgmstream-cli.exe is not installed.";
                return false;
            }

            string tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "FrostyVgmstream",
                Guid.NewGuid().ToString("N"));
            string sourcePath = Path.Combine(tempDirectory, "input.sps");
            string outputPath = Path.Combine(tempDirectory, "output.wav");

            try
            {
                Directory.CreateDirectory(tempDirectory);
                File.WriteAllBytes(sourcePath, source);

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = decoder,
                    Arguments = "-o \"" + outputPath + "\" \"" + sourcePath + "\"",
                    WorkingDirectory = Path.GetDirectoryName(decoder),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "vgmstream could not be started.";
                        return false;
                    }

                    StringBuilder standardOutput = new StringBuilder();
                    StringBuilder standardError = new StringBuilder();
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                            standardOutput.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                            standardError.AppendLine(args.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(DecodeTimeoutMilliseconds))
                    {
                        process.Kill();
                        error = "vgmstream timed out while decoding the sound.";
                        return false;
                    }
                    process.WaitForExit();

                    if (process.ExitCode != 0 || !File.Exists(outputPath))
                    {
                        error = string.IsNullOrWhiteSpace(standardError.ToString())
                            ? standardOutput.ToString().Trim()
                            : standardError.ToString().Trim();
                        return false;
                    }
                }

                using (WaveFileReader reader = new WaveFileReader(outputPath))
                {
                    if (reader.WaveFormat.BitsPerSample != 16)
                    {
                        error = "vgmstream produced unsupported "
                            + reader.WaveFormat.BitsPerSample + "-bit audio.";
                        return false;
                    }

                    sampleRate = reader.WaveFormat.SampleRate;
                    channels = reader.WaveFormat.Channels;
                    using (MemoryStream decodedBytes = new MemoryStream())
                    {
                        byte[] buffer = new byte[65536];
                        int bytesRead;
                        while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                            decodedBytes.Write(buffer, 0, bytesRead);

                        int sampleCount = checked((int)(decodedBytes.Length / sizeof(short)));
                        samples = new short[sampleCount];
                        Buffer.BlockCopy(
                            decodedBytes.GetBuffer(),
                            0,
                            samples,
                            0,
                            sampleCount * sizeof(short));
                    }
                }

                return channels > 0 && sampleRate > 0 && samples.Length > 0;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                        Directory.Delete(tempDirectory, true);
                }
                catch
                {
                }
            }
        }
    }
}
