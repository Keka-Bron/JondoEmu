# Antivirus warnings

Some antivirus engines flag the release's **launcher** as a trojan. It is a false positive: the
detections are generic heuristics, and the executable is built by GitHub Actions from this
repository's public source.

## What was seen (v0.1.0, November 2026)

`Jondo-Full-v0.1.0-win-x64.zip` on VirusTotal: 22 of 67 engines, almost all for one file inside it,
`Jondo Emulator Launcher.exe` (24/67). The others barely: `Jondo Studio.exe` 3/71, `Jondo Server.exe`
2/71, `JondoFix.dll` 0/71, the data 0.

- The labels are generic: `Gen:Variant.Midie`, `Trojan.Midie`, `Win64:Malware-gen`, `TR/W64.Malware`,
  `Mal/Generic-S`, `Malicious (score: 99)`. None names a known malware family.
- Seven of the 22 are one engine sold under several names: Bitdefender's `Gen:Variant.Midie.189089`
  is also ALYac's, Arcabit's, Emsisoft's, GData's and VIPRE's. Avast and AVG are one engine as well, and
  so are TrendMicro and TrendMicro-HouseCall.
- On VirusTotal, Microsoft Defender, Kaspersky, ESET, CrowdStrike, Malwarebytes, Elastic, Dr.Web
  and ClamAV found nothing. A day later, once the sample had been passed around malware
  collections, Microsoft Defender started quarantining that same launcher as
  `Trojan:Win32/Yomal!rfn`, while a fresh build of the same code still scanned clean.
- The zip was uploaded by `github-actions[bot]` from commit `3e672d3` and never replaced.

## Why the launcher looks suspicious to a heuristic

Nothing in it is hidden, but together its habits resemble a loader's:

- It is one `.exe` with the program appended to it (`PublishSingleFile`), which on start unpacks
  native DLLs (Avalonia's Skia and ANGLE) into `%TEMP%\.net` and loads them
  (`IncludeNativeLibrariesForSelfExtract`).
- It keeps the session encrypted with Windows' DPAPI (`SecretStore`).
- It starts another program (`Dofus.exe`) with environment variables of its own, and finds and
  maximizes that program's window.
- It listens on local ports to relay the client to a remote server (`RemoteRelay`) and downloads
  files (the optional HD packs, from Ankama's CDN).
- It was not signed, carried no Windows manifest and no publisher in its file properties.

## What the build does now

- A manifest in the launcher and the server, as Studio had: `asInvoker` (never asks for
  administrator rights) and Windows 10/11 compatibility.
- Company, product, description, copyright and the release's version in every executable's file
  properties (`Directory.Build.props`).

What would help most and is not done:

- **Code signing.** An unsigned single-file executable is the first thing heuristics weigh. Open
  source projects can get a free certificate through the SignPath Foundation; Azure Trusted Signing
  is the paid route.
- **Reporting each release to the engines that flag it**, below.

## Reporting a false positive

Re-scan on VirusTotal first: engines fix their heuristics often. Then, with the file and its
SHA-256, write to those that still flag it. The contacts are the ones VirusTotal lists
(<https://docs.virustotal.com/docs/false-positive-contacts>):

| Engine | Contact |
|:---|:---|
| Bitdefender (also ALYac, Arcabit, Emsisoft, GData, VIPRE) | virus_submission@bitdefender.com |
| Avast, AVG | DL-Virus@gendigital.com · <http://www.avg.com/submit-sample> |
| TrendMicro | <https://www.trendmicro.com/en_us/about/legal/detection-reevaluation.html> |
| AhnLab-V3 | v3sos@ahnlab.com |
| Avira, WithSecure | <https://www.avira.com/en/analysis/submit-url> · <https://www.withsecure.com/en/support/contact-support/submit-a-sample> |
| Sophos | samples@sophos.com |
| Trellix | datasubmission@trellix.com |
| Panda | falsepositives@pandasecurity.com |
| Google | google-at-virustotal@google.com |
| Lionic | <https://www.lionic.com/supports/report-false-positive/> |
| Alibaba, AliCloud | virustotal@list.alibaba-inc.com · antivirus@alibabacloud.com |
| Varist, Zillya, Cynet, CTX | support@varist.com · support@zillya.com · soc@cynet.com · root@malwares.com |

Bitdefender, Avast and TrendMicro alone account for eleven of the twenty-two; they were written to
on 3 November 2026 with the SHA-256 and the links. Microsoft takes reports at
<https://www.microsoft.com/en-us/wdsi/filesubmission>, signed in with a Microsoft account and with the
file itself.
