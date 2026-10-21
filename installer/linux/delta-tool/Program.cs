using System;
using System.IO;

namespace Asyncronus.Setup
{
    /// <summary>
    /// AsyncronusDelta apply VANILLA.dll IN.patch OUT.dll   (verifies the sha256 of the input and of the result)
    /// AsyncronusDelta sha256 FILE
    /// Exit codes: 0 ok, 1 error, 2 usage.
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 && args[0] == "apply")
                {
                    File.WriteAllBytes(args[3], Delta.Apply(File.ReadAllBytes(args[1]), args[2]));
                    Console.WriteLine(Delta.Sha256Hex(args[3]));
                    return 0;
                }
                if (args.Length == 2 && args[0] == "sha256") { Console.WriteLine(Delta.Sha256Hex(args[1])); return 0; }
                Console.Error.WriteLine("Usage: AsyncronusDelta apply VANILLA.dll IN.patch OUT.dll | sha256 FILE");
                return 2;
            }
            catch (Exception e) { Console.Error.WriteLine("ERROR: " + e.Message); return 1; }
        }
    }
}
