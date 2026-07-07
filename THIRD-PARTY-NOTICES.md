# Third-Party Notices

**miPDFsign Community** is licensed under the **GNU Affero General Public License v3.0**
(AGPL-3.0, see [`LICENSE`](./LICENSE)). It uses and distributes the third-party components
listed below under their respective licenses. This file fulfills the associated
attribution and redistribution obligations.

| Component | Version | License | Copyright / Publisher |
|---|---|---|---|
| [iText](https://itextpdf.com/) | 9.6.0 | **AGPL-3.0** (or commercial) | © Apryse / iText Group NV |
| [BouncyCastle.Cryptography](https://www.bouncycastle.org/) | 2.6.2 | MIT (X11 style) | © Legion of the Bouncy Castle Inc. |
| [PdfiumViewer](https://github.com/pvginkel/PdfiumViewer) | 2.13.0 | Apache License 2.0 | © Pieter van Ginkel |
| PDFium (native, `PdfiumViewer.Native.*`) | 2018.4.8.256 | BSD 3-Clause | © Google Inc. / The Chromium Authors |
| System.Drawing.Common | 8.0.28 | MIT | © .NET Foundation and Contributors |
| System.Configuration.ConfigurationManager | 8.0.1 | MIT | © .NET Foundation and Contributors |

> **Note on iText (AGPL):** iText is dual-licensed under **AGPL-3.0** or a commercial
> license. Because miPDFsign Community links iText under the AGPL, the **entire application
> is distributed under the AGPL-3.0** — the complete corresponding source must be made
> available to recipients. To build a **closed-source** product on this code base instead,
> a **commercial iText license** (and removal of any other AGPL obligations) is required.
> See <https://itextpdf.com/how-buy>.

---

## MIT License

Applies to **BouncyCastle.Cryptography**, **System.Drawing.Common**, and
**System.Configuration.ConfigurationManager**.

```
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

---

## BSD 3-Clause License

Applies to the native **PDFium** (Google / The Chromium Authors).

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

   * Redistributions of source code must retain the above copyright
     notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above copyright
     notice, this list of conditions and the following disclaimer in the
     documentation and/or other materials provided with the distribution.
   * Neither the name of Google Inc. nor the names of its contributors may
     be used to endorse or promote products derived from this software
     without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

---

## Apache License 2.0

Applies to **PdfiumViewer** (© Pieter van Ginkel).

This component is licensed under the Apache License, Version 2.0. The full
license text is available at: https://www.apache.org/licenses/LICENSE-2.0

Excerpt (§4 – redistribution obligations, paraphrased): When redistributing, this
license notice and any NOTICE statements must be retained. The software is provided "AS IS"
without warranty.
