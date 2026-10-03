# Builds src\catalogue.json - the list of journals offered in the Journals settings panel.
#
# ISSNs are RESOLVED FROM CROSSREF, not typed from memory: a wrong ISSN produces a journal that
# silently returns nothing forever. Each candidate is looked up by title, the best match taken, and
# then proved by asking for its recent articles. Anything that returns nothing is dropped with a
# note rather than shipped as a dead entry.
#
#   powershell -ExecutionPolicy Bypass -File build-catalogue.ps1

$ErrorActionPreference = 'Stop'
$mail = 'currents@localhost'
$since = (Get-Date).AddDays(-120).ToString('yyyy-MM-dd')

$candidates = @(
    @{ t='New England Journal of Medicine';                      c='General medicine'; tier=1 }
    @{ t='The Lancet';                                           c='General medicine'; tier=1 }
    @{ t='JAMA';                                                 c='General medicine'; tier=1 }
    @{ t='BMJ';                                                  c='General medicine'; tier=1; issn='1756-1833' }
    @{ t='Annals of Internal Medicine';                          c='General medicine'; tier=2 }
    @{ t='Nature Medicine';                                      c='General medicine'; tier=2 }
    @{ t='JAMA Internal Medicine';                               c='General medicine'; tier=2 }

    @{ t='The Lancet Neurology';                                 c='Neurology'; tier=1 }
    @{ t='JAMA Neurology';                                       c='Neurology'; tier=1 }
    @{ t='Neurology';                                            c='Neurology'; tier=2 }
    @{ t='Brain';                                                c='Neurology'; tier=2 }
    @{ t='Annals of Neurology';                                  c='Neurology'; tier=2 }
    @{ t='Journal of Neurology, Neurosurgery & Psychiatry';      c='Neurology'; tier=2; issn='0022-3050' }
    @{ t='European Journal of Neurology';                        c='Neurology'; tier=3 }
    @{ t='Journal of Neurology';                                 c='Neurology'; tier=3 }
    @{ t='Annals of Clinical and Translational Neurology';       c='Neurology'; tier=3 }
    @{ t='Brain Communications';                                 c='Neurology'; tier=3 }
    @{ t='Neurology Clinical Practice';                          c='Neurology'; tier=3 }

    @{ t='Stroke';                                               c='Stroke and cerebrovascular'; tier=1 }
    @{ t='International Journal of Stroke';                      c='Stroke and cerebrovascular'; tier=2 }
    @{ t='European Stroke Journal';                              c='Stroke and cerebrovascular'; tier=2 }
    @{ t='Journal of NeuroInterventional Surgery';               c='Stroke and cerebrovascular'; tier=2 }
    @{ t='Journal of Stroke';                                    c='Stroke and cerebrovascular'; tier=2 }
    @{ t='Cerebrovascular Diseases';                             c='Stroke and cerebrovascular'; tier=3 }
    @{ t='Journal of Stroke and Cerebrovascular Diseases';       c='Stroke and cerebrovascular'; tier=3 }
    @{ t='Stroke: Vascular and Interventional Neurology';        c='Stroke and cerebrovascular'; tier=3 }
    @{ t='Translational Stroke Research';                        c='Stroke and cerebrovascular'; tier=3 }
    @{ t='Frontiers in Stroke';                                  c='Stroke and cerebrovascular'; tier=3 }
    @{ t='Journal of Cerebral Blood Flow & Metabolism';          c='Stroke and cerebrovascular'; tier=3; issn='0271-678X' }

    @{ t='Neurocritical Care';                                   c='Critical care'; tier=2 }
    @{ t='Intensive Care Medicine';                              c='Critical care'; tier=2 }
    @{ t='Critical Care Medicine';                               c='Critical care'; tier=3 }
    @{ t='Critical Care';                                        c='Critical care'; tier=3 }

    @{ t='Circulation';                                          c='Cardiovascular'; tier=2 }
    @{ t='Journal of the American College of Cardiology';        c='Cardiovascular'; tier=2 }
    @{ t='European Heart Journal';                               c='Cardiovascular'; tier=2 }
    @{ t='JAMA Cardiology';                                      c='Cardiovascular'; tier=3 }
    @{ t='Circulation: Cardiovascular Interventions';            c='Cardiovascular'; tier=3 }
    @{ t='Hypertension';                                         c='Cardiovascular'; tier=3 }

    @{ t='American Journal of Neuroradiology';                   c='Neuroimaging and neurosurgery'; tier=3 }
    @{ t='Radiology';                                            c='Neuroimaging and neurosurgery'; tier=3 }
    @{ t='Neuroradiology';                                       c='Neuroimaging and neurosurgery'; tier=3 }
    @{ t='Journal of Neurosurgery';                              c='Neuroimaging and neurosurgery'; tier=3 }
    @{ t='Neurosurgery';                                         c='Neuroimaging and neurosurgery'; tier=3 }

    @{ t='Epilepsia';                                            c='Neurology subspecialties'; tier=3 }
    @{ t='Movement Disorders';                                   c='Neurology subspecialties'; tier=3 }
    @{ t='Multiple Sclerosis Journal';                           c='Neurology subspecialties'; tier=3 }
    @{ t="Alzheimer's & Dementia";                               c='Neurology subspecialties'; tier=3; issn='1552-5260' }
    @{ t='Neuro-Oncology';                                       c='Neurology subspecialties'; tier=3 }
    @{ t='Headache: The Journal of Head and Face Pain';          c='Neurology subspecialties'; tier=3 }
    @{ t='Muscle & Nerve';                                       c='Neurology subspecialties'; tier=3; issn='0148-639X' }
    @{ t='Sleep';                                                c='Neurology subspecialties'; tier=3 }
    @{ t='Journal of Neuroinflammation';                         c='Neurology subspecialties'; tier=3 }
    @{ t='eNeurologicalSci';                                     c='Neurology subspecialties'; tier=3 }
)

$out = New-Object System.Collections.Generic.List[object]
$dropped = New-Object System.Collections.Generic.List[string]

foreach ($cand in $candidates) {
    # An explicit ISSN skips the title lookup. Needed because titles containing "&" do not match in
    # Crossref's journal search, and because a bare query like "BMJ" matches an obsolete print ISSN
    # that deposits nothing. These were all verified by hand before being pinned.
    if ($cand.issn) {
        Start-Sleep -Milliseconds 220
        try {
            $r = Invoke-RestMethod ("https://api.crossref.org/journals/{0}/works?filter=from-created-date:{1},type:journal-article&rows=1&select=container-title&mailto={2}" -f `
                     $cand.issn, $since, $mail) -TimeoutSec 40
            $n = $r.message.'total-results'
        } catch { $n = -1 }
        if ($n -le 0) { $dropped.Add("$($cand.t) [$($cand.issn)] - pinned ISSN returns nothing"); continue }
        $out.Add([ordered]@{ issn = $cand.issn; name = $cand.t; category = $cand.c; tier = $cand.tier; recent = $n })
        "{0,-52} {1,-11} {2,5} in 120d  (pinned)" -f $cand.t, $cand.issn, $n
        continue
    }

    Start-Sleep -Milliseconds 220
    try {
        $hits = (Invoke-RestMethod ("https://api.crossref.org/journals?query={0}&rows=6&mailto={1}" -f `
                   [uri]::EscapeDataString($cand.t), $mail) -TimeoutSec 40).message.items
    } catch { $dropped.Add("$($cand.t) - lookup failed"); continue }
    if (-not $hits) { $dropped.Add("$($cand.t) - not in Crossref"); continue }

    # Exact title match first; otherwise the closest by length, which avoids picking a journal whose
    # name merely contains the query ("Stroke" vs "Journal of Stroke Research").
    $pick = $hits | Where-Object { $_.title -eq $cand.t } | Select-Object -First 1
    if (-not $pick) { $pick = $hits | Sort-Object { [math]::Abs($_.title.Length - $cand.t.Length) } | Select-Object -First 1 }

    $issn = $pick.ISSN | Select-Object -First 1
    if (-not $issn) { $dropped.Add("$($cand.t) - no ISSN"); continue }

    # Prove it: a journal that deposits nothing is worse than absent, because it looks subscribed
    # and silently contributes nothing.
    Start-Sleep -Milliseconds 220
    try {
        $n = (Invoke-RestMethod ("https://api.crossref.org/journals/{0}/works?filter=from-created-date:{1},type:journal-article&rows=0&mailto={2}" -f `
                 $issn, $since, $mail) -TimeoutSec 40).message.'total-results'
    } catch { $n = -1 }
    if ($n -le 0) { $dropped.Add("$($cand.t) [$issn] - no articles in 120 days"); continue }

    $out.Add([ordered]@{ issn = $issn; name = $pick.title; category = $cand.c; tier = $cand.tier; recent = $n })
    "{0,-52} {1,-11} {2,5} in 120d" -f $pick.title, $issn, $n
}

$json = [ordered]@{
    _comment = @(
        "Journal catalogue offered in the Journals settings panel. Generated by build-catalogue.ps1,",
        "which resolves every ISSN from Crossref and then proves it returns recent articles.",
        "Subscribing to a journal copies it into config.json; this file is only the menu."
    )
    generated = (Get-Date -Format 'yyyy-MM-dd')
    journals  = $out
}
$dest = Join-Path $PSScriptRoot 'src\catalogue.json'
[System.IO.File]::WriteAllText($dest, ($json | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding $false))

Write-Output ""
Write-Output "$($out.Count) journals written to $dest"
if ($dropped.Count) { Write-Output "`nDropped:"; $dropped | ForEach-Object { "  $_" } }
