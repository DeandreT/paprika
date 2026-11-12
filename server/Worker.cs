namespace PaperworkClassifier;

public static class Worker
{
    public static Page MakePage(Packet p, Source s, int number, string text) =>
        new()
        {
            SourceId = s.Id,
            SourceName = s.Name,
            PageNumber = number,
            Text = text,
            PreviewUrl = $"/api/packets/{p.Id}/sources/{s.Id}",
        };

    public static List<Document> Assemble(List<Page> pages)
    {
        var result = new List<Document>();
        foreach (var page in pages)
        {
            if (result.Count == 0 || page.StartProbability >= .5)
                result.Add(new() { Type = page.PredictedType });
            result[^1].PageIds.Add(page.Id);
        }
        foreach (var doc in result)
        {
            doc.ReviewReasons = ["Manual approval required."];
            if (
                doc.PageIds.Select(id => pages.Single(p => p.Id == id).PredictedType)
                    .Distinct()
                    .Count() > 1
            )
                doc.ReviewReasons.Add("Page predictions differ within this document.");
            if (doc.Type == "unknown")
                doc.ReviewReasons.Add("Document type is unknown.");
        }
        return result;
    }

    public static Packet Demo(Store store)
    {
        var p = new Packet { Name = "Synthetic logistics packet", Mode = "demo" };
        var source = new Source
        {
            Name = "synthetic-logistics.pdf",
            Extension = ".pdf",
            PageCount = 5,
        };
        p.Sources.Add(source);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SourcePath(p, source))!);
        string[] texts =
        [
            "SYNTHETIC DEMO - NOT A REAL SHIPMENT\nBILL OF LADING - SAMPLE-BOL-001\nPage 1 of 2\nShipper: Example Warehouse\nConsignee: Example Distribution\n24 pallets - dry goods",
            "SYNTHETIC DEMO - NOT A REAL SHIPMENT\nBILL OF LADING - SAMPLE-BOL-001\nPage 2 of 2 - continuation\nAdditional shipment terms\nCarrier signature: SAMPLE",
            "SYNTHETIC DEMO - NOT A REAL SHIPMENT\nPACKING LIST - SAMPLE-PL-001\nSKU DEMO-A: 120 cartons\nSKU DEMO-B: 80 cartons",
            "SYNTHETIC DEMO - NOT A REAL SHIPMENT\nPROOF OF DELIVERY - SAMPLE-POD-001\nReceived 24 pallets\nSigned: SAMPLE RECEIVER",
            "SYNTHETIC DEMO - NOT A REAL SHIPMENT\nCounter slip\nTotal 42.00\nThank you\nReference unreadable - requires human review",
        ];
        Pdfs.Synthetic(store.SourcePath(p, source), texts);
        source.Sha256 = Pdfs.SourceHash(store.SourcePath(p, source));
        p.PromptVersion = "synthetic-demo-1";
        p.TaxonomyVersion = Contract.TaxonomyVersion;
        p.Processing = new("queued", "synthetic-demo");
        string[] types = ["bol", "bol", "packing_list", "pod", "unknown"];
        for (int i = 0; i < texts.Length; i++)
        {
            var page = MakePage(p, source, i + 1, texts[i]);
            page.PredictedType = types[i];
            page.Confidence = i == 4 ? .44 : .94;
            page.StartProbability =
                i == 1 ? .08
                : i == 4 ? .61
                : .97;
            page.ReviewReasons =
            [
                "Synthetic illustrative scores, not calibrated model results.",
                "Manual approval required.",
            ];
            if (i == 4)
                page.ReviewReasons.Add("Uncertain type and boundary.");
            p.Pages.Add(page);
        }
        store.Accept(p, null);
        return p;
    }
}
