using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

public sealed class CleanupAppLockerEvidence
{
    public const string Provider = "Microsoft-Windows-AppLocker";
    public const string Channel = "Microsoft-Windows-AppLocker/EXE and DLL";
    private readonly object gate = new object();
    private readonly List<XmlDocument> records = new List<XmlDocument>();
    private readonly HashSet<long> recordIds = new HashSet<long>();
    private readonly long minimumRecordId;
    private long appliedRecordId = -1;
    private bool policyError;

    public CleanupAppLockerEvidence(long minimumRecordId)
    {
        if (minimumRecordId < 0) throw new ArgumentOutOfRangeException("minimumRecordId");
        this.minimumRecordId = minimumRecordId;
    }

    public string Query
    {
        get { return QueryAfter(minimumRecordId); }
    }

    public string QueryAfter(long recordId)
    {
        if (recordId < minimumRecordId) throw new ArgumentOutOfRangeException("recordId");
        return "*[System[Provider[@Name='" + Provider +
            "'] and (EventID=8000 or EventID=8001 or EventID=8002 or EventID=8003 or EventID=8004)" +
            " and EventRecordID > " + recordId.ToString(CultureInfo.InvariantCulture) + "]]";
    }

    public string ReconcileAndRead(Func<IEnumerable<string>> queryAvailable, Func<string> read)
    {
        string result = read();
        if (result.Length != 0) return result;
        foreach (string xml in queryAvailable()) Observe(xml);
        return read();
    }

    public bool PolicyApplied
    {
        get { lock (gate) return appliedRecordId > minimumRecordId && !policyError; }
    }

    public long AppliedRecordId
    {
        get { lock (gate) return appliedRecordId; }
    }

    public void RequireHealthyPolicy()
    {
        lock (gate)
            if (policyError)
                throw new InvalidOperationException("AppLocker reported policy conversion failure (8000).");
    }

    private static string Value(XmlDocument document, string path)
    {
        var node = document.SelectSingleNode(path);
        return node == null ? string.Empty : node.InnerText;
    }

    private static string SystemValue(XmlDocument document, string name)
    {
        return Value(document, "/*[local-name()='Event']/*[local-name()='System']/*[local-name()='" + name + "']");
    }

    private static string PayloadValue(XmlDocument document, string name)
    {
        return Value(document, "//*[local-name()='" + name +
            "' or (local-name()='Data' and @Name='" + name + "')]");
    }

    public bool Observe(string xml)
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(xml);
        var provider = document.SelectSingleNode(
            "/*[local-name()='Event']/*[local-name()='System']/*[local-name()='Provider']/@Name");
        long recordId;
        int id;
        if (provider == null || provider.Value != Provider || SystemValue(document, "Channel") != Channel ||
            !long.TryParse(SystemValue(document, "EventRecordID"), out recordId) || recordId <= minimumRecordId ||
            !int.TryParse(SystemValue(document, "EventID"), out id) || id < 8000 || id > 8004)
            return false;
        lock (gate)
        {
            if (!recordIds.Add(recordId)) return false;
            records.Add(document);
            if (id == 8000) policyError = true;
            if (id == 8001) appliedRecordId = Math.Max(appliedRecordId, recordId);
        }
        return true;
    }

    public string FindExecution(string path, int expectedId, string actorSid, string systemDrive, string expectedRuleId)
    {
        if (expectedId != 8002 && expectedId != 8004)
            throw new ArgumentOutOfRangeException("expectedId", "Audit events are not enforcing execution proof.");
        Guid expectedRule;
        if (!Guid.TryParse(expectedRuleId, out expectedRule))
            throw new ArgumentException("An exact AppLocker rule GUID is required.", "expectedRuleId");
        lock (gate)
        {
            if (policyError) throw new InvalidOperationException("AppLocker reported policy conversion failure (8000).");
            if (appliedRecordId <= minimumRecordId)
                throw new InvalidOperationException("No synchronized AppLocker policy-applied event (8001).");
            foreach (var document in records)
            {
                var recordId = long.Parse(SystemValue(document, "EventRecordID"), CultureInfo.InvariantCulture);
                if (recordId <= appliedRecordId ||
                    SystemValue(document, "EventID") != expectedId.ToString(CultureInfo.InvariantCulture))
                    continue;
                var file = PayloadValue(document, "FilePath");
                if (file.StartsWith("%OSDRIVE%", StringComparison.OrdinalIgnoreCase))
                    file = systemDrive + file.Substring("%OSDRIVE%".Length);
                Guid rule;
                if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(PayloadValue(document, "TargetUser"), actorSid, StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(PayloadValue(document, "RuleId"), out rule) && rule == expectedRule)
                    return document.OuterXml;
            }
        }
        return string.Empty;
    }
}
