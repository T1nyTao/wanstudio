using System;
using Windows.Security.Credentials;

const string Resource = "WanStudio.DashScope";

static string Mask(string k) => k.Length <= 8 ? "****" : k[..4] + new string('*', Math.Min(12, k.Length - 8)) + k[^4..];

try
{
    if (args.Length < 2) { Console.WriteLine("usage: qa-key --set|--get|--delete <profile> [key]"); return 2; }
    var op = args[0];
    var profile = args[1];
    var vault = new PasswordVault();

    switch (op)
    {
        case "--set":
            if (args.Length < 3) { Console.WriteLine("--set needs a key"); return 2; }
            try { var old = vault.Retrieve(Resource, profile); vault.Remove(old); } catch { }
            vault.Add(new PasswordCredential(Resource, profile, args[2].Trim()));
            Console.WriteLine($"set {profile} => {Mask(args[2].Trim())}");
            return 0;
        case "--get":
            var cred = vault.Retrieve(Resource, profile);
            cred.RetrievePassword();
            Console.WriteLine($"get {profile} => {Mask(cred.Password)} (len={cred.Password.Length})");
            return 0;
        case "--delete":
            try { var c = vault.Retrieve(Resource, profile); vault.Remove(c); Console.WriteLine($"deleted {profile}"); }
            catch { Console.WriteLine($"no credential for {profile}"); }
            return 0;
        default:
            Console.WriteLine("unknown op");
            return 2;
    }
}
catch (Exception ex)
{
    Console.WriteLine("ERROR " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}
