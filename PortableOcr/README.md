# OCR portátil do CratePilot

Fallback offline C#/.NET Framework 4.8, sem wrapper Tesseract gerenciado, sem NuGet em runtime e sem instalar idiomas OCR ou redistribuíveis Windows. Implementação e payload limitados a `PortableOcr/**`.

## Contrato de integração

Namespace `CratePilot.PortableOcr`:

```csharp
public sealed class PortableOcrWord
{
    public string Text { get; set; }
    public System.Drawing.RectangleF Bounds { get; set; }
}

public sealed class TesseractRecognizer : System.IDisposable
{
    public static bool TryCreate(out TesseractRecognizer recognizer, out string error);
    public System.Collections.Generic.IReadOnlyList<PortableOcrWord> Recognize(System.Drawing.Bitmap bitmap);
    public void Dispose();
}
```

- `TryCreate` retorna erro descritivo sem abrir instalador, navegador ou diálogo.
- Propriedades adicionais: `BackendDescription` e `RuntimeDirectory`.
- `Recognize` é síncrono, serializado por instância; não captura tela, não clica e não acessa o jogo. O chamador fornece e mantém a propriedade do bitmap.
- `PSM_SPARSE_TEXT=11`, engine LSTM inglês, nenhuma whitelist. Leitura de `Mid Grade Crate`, `Remaining`, `OPEN`, `NEXT`, `CLAIM`, `SPAM TAP TO OPEN` e números.
- A cópia BGRA recebe grayscale, contraste linear e inversão automática para fundo escuro; nunca redimensiona. `TessBaseAPISetImage` recebe quatro bytes por pixel e stride explícito. Bounds retornam nas coordenadas originais do bitmap.
- Limites: 16 megapixels, 16.384 pixels por lado, deadline nativo de oito segundos por reconhecimento. O contrato não oferece cancelamento durante uma chamada nativa.
- Strings retornadas são decodificadas com `Encoding.UTF8`/`Marshal.Copy`, sem `PtrToStringUTF8`. Texto, iterator, monitor e engine têm liberação explícita; page iterator é ponteiro emprestado.

## Arquiteturas e dependências

`Native/x64/` contém `tesseract50.dll`, `leptonica-1.82.0.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` e `msvcp140.dll`. `Native/x86/` contém os mesmos arquivos exceto `vcruntime140_1.dll`, não importado pelo engine x86. Ambas as arquiteturas foram executadas nesta máquina Windows x64; x86 utiliza WOW64. Windows 32 bits físico não foi testado.

**A versão do pacote NuGet é 5.2.0; a versão nativa reportada por `TessVersion()` é 5.0.0.** Os nomes originais das DLLs e seus bytes foram preservados. Leptonica é 1.82.0; modelo oficial `tessdata_fast` tag 4.1.0, `eng.traineddata` de 4.113.088 bytes. CRT Microsoft release é 14.44.35211.0, proveniente de `VC/Redist/MSVC/14.44.35112` do Visual Studio 2022 Community local, com Authenticode Microsoft válido verificado.

O payload com ambas as arquiteturas, modelo e seis arquivos de licença soma **18.163.334 bytes**, antes do contêiner PE. Somente DLLs da arquitetura do processo são extraídas. x64 nativo: 7.688.608 bytes; x86 nativo: 6.255.312 bytes. AnyCPU com `Prefer32Bit=false` pode usar os dois payloads; x64 permanece válido como alvo prioritário. Requer Windows 10/11 com .NET Framework 4.8 existente e UCRT do próprio sistema. DLLs Windows/UCRT não são copiadas.

## Recursos incorporados obrigatórios

Compilar `NativeApi.cs`, `PortableRuntime.cs` e `TesseractRecognizer.cs`, com referência a `System.Drawing`. Não é necessário distribuir harnesses, fontes de teste ou relatórios junto do executável.

Os nomes lógicos exatos usados pelo loader são:

| Arquivos | `LogicalName` |
|---|---|
| `PortableOcr/Native/x64/*.dll` | `CratePilot.PortableOcr.Native.x64.%(Filename)%(Extension)` |
| `PortableOcr/Native/x86/*.dll` | `CratePilot.PortableOcr.Native.x86.%(Filename)%(Extension)` |
| `PortableOcr/Data/eng.traineddata` | `CratePilot.PortableOcr.Data.eng.traineddata` |
| Os seis arquivos em `PortableOcr/Licenses/` | `CratePilot.PortableOcr.Licenses.%(Filename)%(Extension)` |

Exemplo, a ser aplicado pelo responsável do `.csproj`:

```xml
<EmbeddedResource Include="PortableOcr\Native\x64\*.dll">
  <LogicalName>CratePilot.PortableOcr.Native.x64.%(Filename)%(Extension)</LogicalName>
</EmbeddedResource>
<EmbeddedResource Include="PortableOcr\Native\x86\*.dll">
  <LogicalName>CratePilot.PortableOcr.Native.x86.%(Filename)%(Extension)</LogicalName>
</EmbeddedResource>
<EmbeddedResource Include="PortableOcr\Data\eng.traineddata">
  <LogicalName>CratePilot.PortableOcr.Data.eng.traineddata</LogicalName>
</EmbeddedResource>
<EmbeddedResource Include="PortableOcr\Licenses\*">
  <LogicalName>CratePilot.PortableOcr.Licenses.%(Filename)%(Extension)</LogicalName>
</EmbeddedResource>
```

## Extração e carregamento

Cache privado e versionado:

```text
%LOCALAPPDATA%/CratePilot/runtime/tess5.2.0-fast4.1.0-vc14.44.35211-r1-x64/
%LOCALAPPDATA%/CratePilot/runtime/tess5.2.0-fast4.1.0-vc14.44.35211-r1-x86/
```

- SHA-256 e tamanho esperados ficam no código `PortableRuntime.Assets`. Recursos e arquivos extraídos são verificados antes de carregar. Alterar payload exige atualizar hashes e `BundleVersion` juntos.
- Mutex por usuário/caminho coordena processos. Arquivo temporário exclusivo é escrito e validado no mesmo diretório, então movido/substituído atomicamente. Apenas nomes do manifesto são substituídos; arquivos não relacionados e versões antigas não são removidos.
- Diretórios/assets reparse-point e path traversal são recusados. DLLs/modelo permanecem abertos com `FileShare.Read`, bloqueando escrita e substituição após verificação durante o processo.
- CRT app-local é carregado primeiro, depois Leptonica e Tesseract, via `LoadLibraryExW` com caminho absoluto e `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS`. Caminho carregado é verificado. Não há `SetDllDirectory`, alteração de `PATH`, alteração de política global de DLLs ou rede em runtime.
- A C API cdecl é resolvida por `GetProcAddress` exclusivamente no handle verificado, sem resolução insegura por basename. Um Tesseract/Leptonica diferente já carregado causa falha explícita. As DLLs permanecem carregadas até o fim do processo; engines individuais são descartáveis.
- As licenças são extraídas para `licenses/`; `eng.traineddata` fica em `tessdata/`.
- O CRT desse engine abre caminhos na code page ANSI Windows. Nomes de perfil não representáveis utilizam o caminho curto nativo, quando este também for representável. Caso contrário, a criação retorna erro explícito; nenhum recurso do Windows é instalado ou alterado. Perfis CJK em sistemas com ACP ocidental podem exigir `<activeCodePage>UTF-8</activeCodePage>` no manifesto da aplicação (Windows 10 1903+); essa decisão cabe ao responsável pelo manifesto. Nesta máquina, caminho com acentos ACP foi inicializado e caminho CJK não representável foi recusado explicitamente.

## Procedência e licenças

- Pacote oficial: <https://api.nuget.org/v3-flatcontainer/tesseract/5.2.0/tesseract.5.2.0.nupkg>, projeto <https://github.com/charlesw/tesseract/tree/5.2.0>. SHA-256 do pacote: `202D82FC7C7D8384DF7DA57206D5E1F456CCDABD648C46E67CDFAA3A911D4795`. Apenas as quatro DLLs nativas x64/x86 foram extraídas; `Tesseract.dll` gerenciado não é usado.
- Tesseract, Apache-2.0: <https://raw.githubusercontent.com/tesseract-ocr/tesseract/5.0.0/LICENSE>.
- Leptonica, BSD-2-Clause: <https://raw.githubusercontent.com/DanBloomberg/leptonica/1.82.0/leptonica-license.txt>.
- Modelo oficial, Apache-2.0: <https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/eng.traineddata> e <https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/LICENSE>. SHA-256: `7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2`.
- Microsoft: <https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution> e <https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files>. DLLs release de `VC/Redist`, não de `System32`, não de `debug_nonredist`, sem modificações. Seus termos próprios não são substituídos pela licença da aplicação.
- Licença Microsoft original: <https://visualstudio.microsoft.com/wp-content/uploads/2021/11/Visual-Studio-2022-Community-License-EN.docx>. O DOCX original é preservado; o TXT adjacente é extração textual dos parágrafos para leitura offline. `Microsoft-VisualStudio-2022-Redist.txt` é a cópia original do apontador de redistribuição do Visual Studio instalado.

Nenhum instalador foi executado. Downloads ocorreram somente durante desenvolvimento; nada é baixado pela aplicação.

## Build e verificação de desenvolvimento

O ponto de entrada mantido para compilar a aplicação é `build.ps1`. Durante trabalho paralelo, executar somente após o handoff coordenado dos responsáveis; não publicar um EXE de uma geração anterior como entrega nova.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

A verificação interna de desenvolvimento usa harnesses externos e temporários em pasta GUID, apenas com bitmaps sintéticos e contratos puros. Eles não são API da aplicação, comando de distribuição nem dependência de runtime; devem ser limpos após a validação e o registro dos resultados. Validar recursos incorporados independentemente do cache, fallback, labels/números, bounds e arquiteturas x64/x86. OCR Windows sem idioma é condição ambiental explícita; fallback ausente ou inválido é falha. Não restaurar diretórios de verificação arquivados. A máquina de referência tem **zero idiomas OCR Windows**. Verificações do EXE devem corresponder ao hash da versão final; execução no jogo e integração WPF exigem validação própria e não são certificadas por fixtures sintéticas.
