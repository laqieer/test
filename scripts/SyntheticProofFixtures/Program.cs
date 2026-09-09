using System.Text.Json;
using FEBuilderGBA.TestFixtures;

if (args.Length != 1)
    throw new ArgumentException("Supply one new owned zipdb-proof.gba destination.");
Console.WriteLine(JsonSerializer.Serialize(SyntheticFe8URom.WriteNew(args[0])));
