# Third-party notices

`ksef` (kod źródłowy i binarki z wydań) zawiera lub wykorzystuje poniższe składniki. Binarka Native AOT
ma w sobie skompilowany kod bibliotek z sekcji 1–3; licencje wymagają dołączenia ich treści.

## 1. KSeF.Client — oficjalna biblioteka Ministerstwa Finansów

<https://github.com/CIRFMF/ksef-client-csharp> (submoduł `external/ksef-client-csharp`), licencja MIT:

```text
Copyright (c) 2025 Ministerstwo Finansów

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## 2. .NET — runtime i biblioteki

Runtime .NET, biblioteki klas oraz pakiety System.CommandLine, Microsoft.Extensions.*,
System.Security.Cryptography.Pkcs i System.Security.Cryptography.Xml
(<https://github.com/dotnet/runtime>, <https://github.com/dotnet/command-line-api>), licencja MIT:

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Runtime .NET zawiera składniki innych autorów, wymienione w
<https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>.

## 3. ASP.NET Core 2.3 (zależności biblioteki MF)

Pakiety Microsoft.AspNetCore.Http.Abstractions, .Http.Extensions, .Http.Features, .Localization
i Microsoft.Net.Http.Headers 2.3.0 (<https://github.com/aspnet/AspNetCore>; licencja wskazana w pakietach: <https://raw.githubusercontent.com/aspnet/AspNetCore/2.0.0/LICENSE.txt>),
Copyright (c) .NET Foundation and Contributors, licencja Apache 2.0:
<https://www.apache.org/licenses/LICENSE-2.0>.

## 4. Schematy i arkusz FA(3)

Schematy XSD faktury FA(3) (`schemas/`) i arkusz wizualizacji `styl.xsl` (`stylesheets/`) to oficjalne
wzory Ministerstwa Finansów z Centralnego Repozytorium Wzorów Dokumentów Elektronicznych
(<http://crd.gov.pl/wzor/2025/06/25/13775/>), zamieszczone bez zmian. Klasy w `src/Ksef.Fa3/Generated/`
są wygenerowane z tych schematów (`dotnet-xscgen`).

## Pakiety pominięte w binarce

Biblioteka MF zależy też od QRCoder, SkiaSharp, Microsoft.Maui.Graphics, System.Drawing.Common
i Microsoft.IdentityModel (MIT) — `ksef` ich nie używa i kompilator Native AOT ich nie dołącza.
