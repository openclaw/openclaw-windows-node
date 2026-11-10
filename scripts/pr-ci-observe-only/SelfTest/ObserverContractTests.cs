using System.Text.Json;
using System.Xml;
using OpenClaw.ObserveOnly;

internal static class ObserverContractTests
{
    public static (int ConfigurationCases, int CustodyCases) Run(string privateRoot)
    {
        if (!Path.IsPathFullyQualified(privateRoot) || Directory.Exists(privateRoot) || File.Exists(privateRoot)) throw new InvalidOperationException("contract-selftest-root");
        Directory.CreateDirectory(privateRoot);
        int configurations = 0, custodyCases = 0;
        void Require(bool value) { if (!value) throw new InvalidOperationException("contract-selftest-failed"); }
        void Reject(Action action) { bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; } catch (InvalidOperationException) { failed = true; } Require(failed); }
        XmlElement Valid()
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml("<Configuration/>");
            var root = doc.DocumentElement!;
            foreach (var pair in new Dictionary<string, string> {
                ["Root"]=privateRoot, ["Invocation"]=new string('a',32), ["ExpectedSource"]=Path.Combine(privateRoot,"source.dll"),
                ["ExpectedSourceSha256"]=new string('b',64), ["ExpectedObjectModelSha256"]=new string('c',64),
                ["ExpectedCollectorSha256"]=new string('d',64), ["ExpectedHostImageSha256"]=new string('e',64),
                ["ExpectedDotnetSha256"]=new string('f',64), ["ExpectedBlameSha256"]=new string('1',64),
                ["Framework"]=".NETCoreApp,Version=v10.0" })
            { var node=doc.CreateElement(pair.Key); node.InnerText=pair.Value; root.AppendChild(node); }
            return root;
        }
        void Bad(Action<XmlElement> mutation) { var xml=Valid(); mutation(xml); Reject(()=>ObserverConfiguration.Parse(xml)); configurations++; }
        string Fresh(string name) { var path=Path.Combine(privateRoot,name); Directory.CreateDirectory(path); return path; }
        ReceiptCustody Bound(string path) { var c=new ReceiptCustody(); c.Bind(path,new string('a',32)); return c; }
        try
        {
            var valid=ObserverConfiguration.Parse(Valid()); Require(valid.Root==privateRoot && valid.Invocation==new string('a',32)); configurations++;
            var canonical=Valid(); canonical["Framework"]!.InnerText=".netcoreapp, Version=10.0, Profile=";
            Require(ObserverConfiguration.Parse(canonical).ExpectedSourceSha256==new string('b',64)); configurations++;
            Bad(x=>x.RemoveChild(x["Framework"]!));
            Bad(x=>x.RemoveChild(x["Root"]!));
            Bad(x=>x.AppendChild(x["Root"]!.CloneNode(true)));
            Bad(x=>x.AppendChild(x["Framework"]!.CloneNode(true)));
            Bad(x=>x.AppendChild(x.OwnerDocument!.CreateElement("Unexpected")));
            Bad(x=>x.SetAttribute("extra","PRIVATE"));
            Bad(x=>x["Root"]!.SetAttribute("extra","PRIVATE"));
            Bad(x=>x["Framework"]!.SetAttribute("extra","PRIVATE"));
            Bad(x=> {x.RemoveChild(x["Framework"]!);var n=x.OwnerDocument!.CreateElement("f","Framework","urn:private");n.InnerText=".NETCoreApp,Version=v10.0";x.AppendChild(n);});
            Bad(x=> {x.RemoveChild(x["Root"]!);var n=x.OwnerDocument!.CreateElement("r","Root","urn:private");n.InnerText=privateRoot;x.AppendChild(n);});
            Bad(x=>x["Framework"]!.InnerXml="<nested>.NETCoreApp,Version=v10.0</nested>");
            Bad(x=>x["Framework"]!.InnerXml="<![CDATA[.NETCoreApp,Version=v10.0]]>");
            Bad(x=>x["Framework"]!.InnerText="");
            Bad(x=>x["Framework"]!.InnerText=new string('x',129));
            Bad(x=>x["Framework"]!.InnerText="PRIVATE_NOT_A_FRAMEWORK");
            Bad(x=>x["Framework"]!.InnerText=".NETCoreApp,Version=v9.0");
            Bad(x=>x["Framework"]!.InnerText=".NETFramework,Version=v10.0");
            Bad(x=>x["Framework"]!.InnerText=".NETCoreApp,Version=v10.0,Profile=Private");
            Bad(x=>x["Invocation"]!.InnerText="PRIVATE");
            Bad(x=>x["ExpectedSourceSha256"]!.InnerText=new string('z',64));
            Bad(x=>x.AppendChild(x.OwnerDocument!.CreateTextNode("PRIVATE")));
            Reject(()=>ObserverConfiguration.Parse(null)); configurations++;

            // Faithful no-write boundary: malformed hosted config never binds the same custody owner used by Observer.
            var cwd=Environment.CurrentDirectory;var isolated=Fresh("unbound-cwd");Directory.SetCurrentDirectory(isolated);
            try
            {
                var c=new ReceiptCustody();var xml=Valid();xml["Framework"]!.InnerText=".NETCoreApp,Version=v9.0";
                Reject(()=> {var config=ObserverConfiguration.Parse(xml);c.Bind(config.Root,config.Invocation);});
                Require(!c.IsBound);Reject(c.CreateClaim);Reject(c.MarkInvalid);Reject(()=>c.WriteNew("observer.json",new { schema=1 }));
                Require(!Directory.EnumerateFileSystemEntries(isolated).Any());custodyCases++;
            }
            finally { Directory.SetCurrentDirectory(cwd); }
            var badNonce=new ReceiptCustody();Reject(()=>badNonce.Bind(privateRoot,"PRIVATE"));Require(!badNonce.IsBound);custodyCases++;
            var relative=new ReceiptCustody();Reject(()=>relative.Bind("relative",new string('a',32)));Require(!relative.IsBound);custodyCases++;
            var c1=Bound(Fresh("claim"));c1.CreateClaim();Require(File.Exists(Path.Combine(privateRoot,"claim","collector.claim")));custodyCases++;
            bool duplicate=false;try {c1.CreateClaim();}catch(IOException){duplicate=true;}Require(duplicate);custodyCases++;
            var rebound=Fresh("rebind");Reject(()=>c1.Bind(rebound,new string('b',32)));Require(!Directory.EnumerateFileSystemEntries(rebound).Any());custodyCases++;
            var receiptRoot=Fresh("receipt");var c2=Bound(receiptRoot);c2.WriteNew("observer.json",new {schema=1, invocation=new string('a',32)});
            var original=File.ReadAllBytes(Path.Combine(receiptRoot,"observer.json"));using(var json=JsonDocument.Parse(original))Require(json.RootElement.GetProperty("schema").GetInt32()==1);custodyCases++;
            duplicate=false;try {c2.WriteNew("observer.json",new {schema=2});}catch(IOException){duplicate=true;}Require(duplicate&&original.SequenceEqual(File.ReadAllBytes(Path.Combine(receiptRoot,"observer.json"))));custodyCases++;
            Reject(()=>c2.WriteNew("../outside.json",new {schema=1}));Require(!File.Exists(Path.Combine(privateRoot,"outside.json")));custodyCases++;
            var largeRoot=Fresh("oversize");var large=Bound(largeRoot);Reject(()=>large.WriteNew("observer.json",new {text=new string('x',16385)}));Require(!Directory.EnumerateFileSystemEntries(largeRoot).Any());custodyCases++;
            var markerRoot=Fresh("marker");var marker=Bound(markerRoot);marker.MarkInvalid();Require(File.ReadAllText(Path.Combine(markerRoot,"observer-invalid"))=="invalid");custodyCases++;
            duplicate=false;try {marker.MarkInvalid();}catch(IOException){duplicate=true;}Require(duplicate&&File.ReadAllText(Path.Combine(markerRoot,"observer-invalid"))=="invalid");custodyCases++;
        }
        finally { Directory.Delete(privateRoot,true); }
        return (configurations,custodyCases);
    }
}
