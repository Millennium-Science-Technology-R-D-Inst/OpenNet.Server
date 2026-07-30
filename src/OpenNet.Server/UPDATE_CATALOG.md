# OpenNet update catalog

The desktop client requests:

`GET /api/v1/update/latest?channel=stable&architecture=x64&packageType=installer`

Configure releases in the server's `UpdateCatalog:Packages` array. Keep a separate
entry for every architecture and package type:

```json
{
  "UpdateCatalog": {
    "CacheDurationSeconds": 300,
    "Packages": [
      {
        "Version": "1.2.0",
        "Channel": "stable",
        "Architecture": "x64",
        "PackageType": "installer",
        "Validation": "64-character SHA-256 in hexadecimal",
        "ReleaseNotes": "Summary shown in the Settings page.",
        "PublishedAtUtc": "2026-07-30T00:00:00Z",
        "IsActive": true,
        "Mirrors": [
          {
            "Url": "https://downloads.example.com/OpenNet-1.2.0-x64.exe",
            "MirrorName": "Primary",
            "MirrorType": "Direct"
          }
        ]
      }
    ]
  }
}
```

Supported mirror types are `Direct`, `Archive`, and `Browser`. The client tries
them in that order. `Validation` is mandatory so that an incomplete release
record cannot be presented as an update.
