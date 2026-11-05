using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        try
        {
            var asm = Assembly.LoadFrom(@"c:\Users\js\ETS2 Plugin\bin\Debug\bin\ETS2Plugin.dll");
            Console.WriteLine("Embedded resources:");
            foreach (var name in asm.GetManifestResourceNames())
            {
                Console.WriteLine("Resource: " + name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
        }
    }
}
