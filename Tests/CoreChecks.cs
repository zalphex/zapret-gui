using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;

internal static class CoreChecks
{
    private static int passed;
    private static void Assert(bool ok, string name)
    {
        if (!ok)
            throw new Exception(name);
        passed++;
        Console.WriteLine("OK " + name);
    }

    private static void Reject(Action bad, string name)
    {
        try
        {
            bad();
        }
        catch (IOException)
        {
            Assert(true, name);
            return;
        }
        catch (InvalidDataException)
        {
            Assert(true, name);
            return;
        }

        throw new Exception(name);
    }

    private static void ValidateArchive(string name, bool valid)
    {
        var zip = Disk.NewFile(Paths.Root);
        var dest = Disk.NewFile(Paths.Root);
        using (var file = File.Create(zip))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var e = archive.CreateEntry(name);
            using (var w = new StreamWriter(e.Open()))
                w.Write("test");
        }

        if (valid)
        {
            Downloads.Extract(zip, dest);
            Assert(File.ReadAllText(Path.Combine(dest, name)) == "test", "valid archive");
        }
        else
            Reject(() => Downloads.Extract(zip, dest), "refuse " + name);
    }

    public static int Main(string[] args)
    {
        Directory.CreateDirectory(Paths.Root);
        try
        {
            string bat = "set dummy=foo\r\nstart \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=%GameFilterTCP% ^\r\n --wf-udp=%GameFilterUDP% --hostlist=\"%LISTS%user.txt\"\r\n";
            var g = new GamePorts
            {
                Mode = "udp",
                Tcp = "80,443",
                Udp = "1200-1300"
            };
            string parsed = Strategies.Parse(bat, Paths.Zap, g);
            Assert(parsed.StartsWith("--wf-tcp=12") && parsed.Contains("--wf-udp=1200-1300") && !parsed.Contains("%"), "strategy and game variables");
            Reject(() => Strategies.Parse(bat + bat, Paths.Zap, g), "ambiguous strategy");
            Reject(() => Strategies.Parse(bat.Replace("%LISTS%", "%missing%"), Paths.Zap, g), "unsupported variable");
            if (args.Length > 0)
            {
                var samples = Directory.GetFiles(args[0], "general*.bat");
                Assert(samples.Length > 0, "upstream fixtures present");
                foreach (string path in samples)
                {
                    var text = Strategies.Parse(File.ReadAllText(path), Paths.Zap, g);
                    Assert(text.Contains("--wf-") && !text.Contains("%") && !text.Contains("^"), Path.GetFileName(path));
                }
            }

            if (args.Length >= 4)
            {
                var zap = Downloads.ParseRelease(Component.Zapret, File.ReadAllText(args[1]));
                var tg = Downloads.ParseRelease(Component.TG, File.ReadAllText(args[2]));
                Assert(zap.Name.EndsWith(".zip") && zap.Size > 0, "official zapret release");
                Assert(tg.Name == "TgWsProxy_windows.exe" && tg.Size > 0, "official TG release");
                Assert(AddressLists.Encode(AddressLists.Names[2], File.ReadAllText(args[3])).Length > 0, "official IPSet");
            }

            GamePorts.Check("1,443,1024-65535");
            foreach (string s in new[]
            {
                "0",
                "65536",
                "-5",
                "22-11",
                "1,,2",
                "*"
            }

            )
                Reject(() => GamePorts.Check(s), "reject ports " + s);
            Disk.Write(GamePorts.FileName, g.Encode());
            var restored = GamePorts.Load();
            Assert(restored.Mode == g.Mode && restored.Udp == g.Udp, "game settings round trip");
            foreach (string s in new[]
            {
                "1.2.3.4/0",
                "2001:db8::1/128",
                "::1",
                "192.0.2.1/32"
            }

            )
                Assert(AddressLists.IsAddress(s), "valid " + s);
            foreach (string s in new[]
            {
                "1",
                "1.2.3.4/33",
                "::1/129",
                "999.2.3.4",
                "example.org",
                "127.0.0.1/-1"
            }

            )
                Assert(!AddressLists.IsAddress(s), "invalid " + s);
            Assert(AddressLists.Encode(AddressLists.Names[0], "example.org\n# note").Length > 0, "domain list");
            Reject(() => AddressLists.Encode(AddressLists.Names[2], "# empty"), "empty active ipset");
            Reject(() => AddressLists.Encode(AddressLists.Names[2], AddressLists.EmptyIp), "reserved ipset marker");
            AddressLists.Ensure();
            Assert(AddressLists.Names.Count(n => File.Exists(Path.Combine(Paths.Lists, n))) == 3, "first-run lists");
            Disk.Write(AddressLists.Active, Disk.Utf8.GetBytes(AddressLists.EmptyIp + "\r\n"));
            var list = AddressLists.Read(AddressLists.Names[2]);
            Assert(list.Inactive && list.File == AddressLists.Backup, "inactive IP set edit target");
            AddressLists.Save(list, "203.0.113.8/32");
            Assert(AddressLists.Mode(AddressLists.Active) == "none" && File.ReadAllText(AddressLists.Backup).Contains("203.0.113.8"), "editing preserves disabled mode");
            Reject(() => AddressLists.Save(list, "192.0.2.1"), "stale edit protection");
            Disk.Remember("Zapret", "custom.bat");
            Disk.Remember("TGVersion", "v1");
            Assert(Disk.State("Zapret") == "custom.bat", "preserve legacy state");
            var rows = new[]
            {
                new Trial
                {
                    Name = "one",
                    First = 2,
                    Mean = 1
                },
                new Trial
                {
                    Name = "both",
                    First = 1,
                    Second = 1,
                    Mean = 120
                },
                new Trial
                {
                    Name = "fast",
                    First = 1,
                    Second = 2,
                    Mean = 50
                },
                new Trial
                {
                    Name = "slow",
                    First = 1,
                    Second = 2,
                    Mean = 80
                }
            };
            Assert(Trial.Winner(rows.Take(2)).Name == "both", "coverage first");
            Assert(Trial.Winner(rows).Name == "fast", "success count then latency");
            Assert(Trial.Winner(new[] { new Trial { Name = "failed" } }) == null, "no false winner");
            var hosts = Maintenance.MergeHosts("127.0.0.1 local\r\n", "192.0.2.1 example.org");
            Assert(Maintenance.MergeHosts(hosts, "192.0.2.1 example.org") == hosts && hosts.Contains("127.0.0.1 local"), "idempotent hosts merge");
            Reject(() => Maintenance.MergeHosts("# zapretgui: Flowseal hosts begin", "192.0.2.1 a.example"), "incomplete hosts block");
            ValidateArchive("bin/app.txt", true);
            ValidateArchive("../outside", false);
            ValidateArchive("C:/escape", false);
            ValidateArchive("..\\outside", false);
            Disk.Write(Paths.TgExe, new byte[] { 0 });
            ProxySettings.Ensure();
            var original = File.ReadAllBytes(Paths.Config);
            ProxySettings.Ensure();
            Assert(File.ReadAllBytes(Paths.Config).SequenceEqual(original), "keep existing proxy secret");
            Assert(ProxySettings.Link().StartsWith("tg://proxy?server=127.0.0.1&port=1443&secret=dd"), "Telegram link");
            ProxySettings.Profile("Без CF");
            Assert(!(bool)ProxySettings.Read()["cfproxy"], "apply proxy profile");
            Console.WriteLine(passed + " checks passed");
            return 0;
        }
        finally
        {
            Directory.Delete(Paths.Root, true);
        }
    }
}
