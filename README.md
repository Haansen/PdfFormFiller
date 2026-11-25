# PdfFormFiller

Ett fristående C#-bibliotek (.NET 8) för PDF-formulär:

- Fyll textfält, kryssrutor, radioknappar, dropdowns och listor via fältnamn
- Spara som **Flattened** (fälten ritas in i sidan och tas bort, inget går att redigera),
  **ReadOnly** (fälten finns kvar men är låsta) eller **Editable**
- Slå ihop flera PDF:er till en, välj sidor, ta bort sidor, byt namn på fält
- Returnerar alltid färdiga `byte[]`

**Inga tredjepartsberoenden.** Parser, writer, filter, dekryptering och fontmätning är egen kod ovanpå .NET:s
basbibliotek (`System.IO.Compression` för Flate, `System.Security.Cryptography` för AES/MD5/SHA).
Texten i fälten ritas med standardfonten Helvetica i WinAnsi-kodning, vilket täcker å, ä, ö och övriga
västeuropeiska tecken i alla läsare. Innehåller ett värde tecken utanför WinAnsi (polska, turkiska, grekiska,
kyrilliska m.fl.) bäddas Liberation Sans in automatiskt som Unicode-font med sökbar text. Tecken som inte finns
i någon av fonterna (t.ex. kinesiska) ritas som `?`, eller ger fel om `ThrowOnUnsupportedCharacters` är satt.

### Vad kärnan klarar

- Klassiska xref-tabeller, xref-strömmar, objektströmmar, hybridfiler, lineariserade filer och inkrementella uppdateringar (`/Prev`-kedjor)
- Trasiga filer: fel `startxref`, förstörd xref-tabell eller felaktiga offset repareras genom att hela filen skannas efter objekt
- Filter: FlateDecode, LZWDecode, ASCIIHexDecode, ASCII85Decode, RunLengthDecode samt PNG- och TIFF-prediktorer. Bildfilter (DCT, JPX, CCITT, JBIG2) passerar orörda
- Kryptering: standardhanteraren med RC4 40/128, AES-128 och AES-256 (R2 till R6). Dokument som öppnas utan lösenord, dvs. med bara ett "ägarlösenord"-skydd, läses. Det gäller även dokument med användar- eller ägarlösenord som anges vid `Load`. Utdata skrivs alltid okrypterat. Certifikatskydd (`Adobe.PubSec`) stöds inte
- Utdata: ren PDF 1.7 med klassisk xref-tabell. Bara objekt som faktiskt används skrivs, så borttagna sidor och fält lämnar inget skräp

## Snabbstart

```csharp
using PdfFormFiller;

byte[] mall = File.ReadAllBytes("ansokan.pdf");

using var doc = PdfFormDocument.Load(mall);

doc.SetText("namn", "Åsa Öberg")
   .SetText("adress", "Storgatan 1\n123 45 Göteborg")      // flerradigt fält radbryts automatiskt
   .SetCheckBox("godkann", true)
   .SetRadio("kon", "kvinna")                               // exportvärde
   .SetChoice("land", "Norge")                              // exportvärde eller visningstext
   .SetValue("belopp", "1 234,50 kr");                      // SetValue väljer rätt typ själv

byte[] klar = doc.Save(OutputMode.Flattened);
```

## API

### Ladda

| Metod | Beskrivning |
|---|---|
| `PdfFormDocument.Load(byte[] pdf)` | Öppna från minne |
| `PdfFormDocument.Load(Stream pdf)` | Öppna från ström |
| `PdfFormDocument.Load(string path)` | Öppna från fil |
| `PdfFormDocument.Load(byte[] pdf, string? password)` | Öppna från minne med lösenord |
| `PdfFormDocument.Load(Stream pdf, string? password)` | Öppna från ström med lösenord |
| `PdfFormDocument.Load(string path, string? password)` | Öppna från fil med lösenord |
| `PdfFormDocument.Merge(IEnumerable<byte[]> docs, bool prefixFieldsWithIndex = false)` | Slå ihop flera PDF:er. Med prefix får fälten namn `0.namn`, `1.namn` osv. Tar inget lösenord. |
| `void Dispose()` | Frigör dokumentet. Anrop efter `Dispose` ger `ObjectDisposedException`. |

Lösenordet kan vara användar- eller ägarlösenord. En fil som öppnas utan lösenord ignorerar det, och `null` fungerar
som utan lösenord. Lösenordet används bara vid öppning och sparas inte. Saknas ett nödvändigt lösenord, eller är det fel, ges
`PdfFormException` (se Felhantering). `Merge` och `Append(byte[] ...)` tar inget lösenord: ladda filen med lösenord
först och använd `Append(PdfFormDocument ...)`.

### Läsa

| Medlem | Beskrivning |
|---|---|
| `int PageCount` | Antal sidor |
| `IReadOnlyList<FormFieldInfo> GetFields()` | Alla fält: `Name`, `Type`, `Value`, `Options`, `OptionTexts`, `Pages` (0-baserat), `ReadOnly`, `Required`, `MultiLine` |
| `string? GetValue(string fieldName)` | Aktuellt värde. I en flervalslista ges första valda värdet. I en radiogrupp med `/Opt` ges exportvärdet. |
| `bool HasField(string fieldName)` | Finns fältet |

`OptionTexts` är visningstexterna för `Options`, samma längd och ordning. Saknar PDF:en en separat visningstext, till
exempel för kryssrutor, är texten exportvärdet.

`FieldType`: `Text`, `CheckBox`, `RadioButton`, `ComboBox`, `ListBox`, `PushButton`, `Signature`, `Unknown`.

### Fylla i

Alla setters returnerar dokumentet så att anrop kan kedjas.

| Metod | Beskrivning |
|---|---|
| `SetValue(string name, string? value)` | Generell. Kryssrutor tolkar `true/false`, `ja/nej`, `yes/no`, `1/0`, `on/off`. Radio och val tar exportvärde eller visningstext. |
| `SetValues(IEnumerable<KeyValuePair<string,string?>>)` | Flera på en gång, t.ex. en `Dictionary<string,string?>` |
| `SetText(string name, string? value)` | Textfält. `null` eller tom sträng tömmer fältet. Radbrytningar respekteras i flerradiga fält. |
| `SetCheckBox(string name, bool isChecked)` | Kryssruta. I en grupp med olika exportvärden kryssar `true` den första rutan. |
| `SetCheckBox(string name, string exportValue)` | Kryssar rutan med angivet exportvärde i en kryssrutegrupp och avmarkerar övriga. |
| `SetRadio(string name, string? exportValue)` | Radiogrupp. Tar exportvärde, visningstext från `/Opt`, eller 0-baserat index. `null` avmarkerar. |
| `SetChoice(string name, string? value)` | Dropdown eller lista. Tar exportvärde eller visningstext. Okänt värde ger fel om fältet inte är redigerbart. |
| `SetTextAlignment(string name, TextAlignment a)` | Vänster, centrerat eller höger för text- och valfält. Ritas om direkt. |
| `GetTextAlignment(string name)` | Aktuell justering (`TextAlignment.Left` om inget är satt) |
| `SetReadOnly(string name, bool readOnly = true)` | Låser ett enskilt fält. Gäller i lägena Editable och ReadOnly. |
| `FlattenFields(params string[] names)` | Plattar bara angivna fält vid sparning i Editable eller ReadOnly. Övriga fält förblir levande. Ingen extra effekt i Flattened, som plattar allt. |
| `ClearFlattenFields()` | Ångrar tidigare `FlattenFields`. |
| `RenameField(string name, string newName)` | Byter namn. Vid punktnotation (`grupp.falt`) byts sista delen. |
| `PrefixFields(string prefix)` | Lägger alla fält under ett prefix (`namn` blir `prefix.namn`). Används före `Append` av samma mall igen. |

Fel: `FieldNotFoundException` (ärver `PdfFormException`) när fältet saknas, `PdfFormException` vid fel typ eller okänt alternativ.
Felmeddelandet listar tillgängliga alternativ.

Lösenordsfel: om filen kräver lösenord och inget har angetts ges `PdfFormException` med meddelandet "The PDF requires a password
to open." Ett felaktigt lösenord ger `PdfFormException` med "The supplied password is incorrect for this PDF." De två
meddelandena skiljer sig åt, så de kan visas olika för användaren.

### Sidor och sammanfogning

| Metod | Beskrivning |
|---|---|
| `RemovePage(int index)` | Tar bort en sida (0-baserat). Fält som bara fanns på sidan försvinner. Sista sidan kan inte tas bort. |
| `RemovePages(params int[] indices)` | Flera sidor |
| `KeepPages(params int[] indices)` | Behåll bara angivna sidor |
| `Append(byte[] pdf, int[]? pageIndices = null, string? fieldPrefix = null)` | Lägg till sidor från en annan PDF, alla eller utvalda. Med `fieldPrefix` får fälten namn `prefix.namn`. Utan prefix får krockande namn suffix `_2`, `_3`. Tar inget lösenord. |
| `Append(PdfFormDocument other, ...)` | Samma, från ett annat laddat dokument, till exempel ett skyddat dokument som laddats med lösenord |
| `Insert(byte[] pdf, int atPageIndex, int[]? pageIndices = null, string? fieldPrefix = null)` | Som `Append`, men sidorna hamnar på angiven position |
| `MovePage(int fromIndex, int toIndex)` | Flyttar en sida |

### Spara

| Metod | Beskrivning |
|---|---|
| `byte[] Save(OutputMode mode = OutputMode.Flattened)` | Färdig PDF som bytes. Dokumentet kan användas vidare efteråt. |
| `void Save(string path, OutputMode mode = OutputMode.Flattened)` | Till fil |
| `void Save(Stream destination, OutputMode mode = OutputMode.Flattened)` | Till ström |

Den sparade filen är alltid okrypterad. Ett skyddat dokument tappar därmed sitt lösenord och sina behörighetsbegränsningar.

**XFA.** Om mallen har en XFA-beskrivning (`/AcroForm /XFA`) tas den bort ur utdata när den annars skulle visa gamla
värden: vid `ReadOnly`, när dokumentet har ändrats sedan det lästes in (setters, `RenameField`, `PrefixFields`,
`FlattenFields`, sid- eller sammanfogningsoperationer), eller när utseenden har skrivits. En oförändrad fil som inte
behövde nya utseenden behåller sin XFA. Ett rent XFA-formulär utan AcroForm-fält rörs aldrig i `Editable` eller
`ReadOnly`. I `Flattened` tas hela AcroForm bort, XFA inräknat.

`OutputMode`:

| Läge | Resultat |
|---|---|
| `Flattened` | Fältens utseende ritas in i sidinnehållet, alla formulärfält tas bort. Standard. |
| `ReadOnly` | Fälten finns kvar med värden men är flaggade read-only. Värdena kan läsas ut maskinellt senare. |
| `Editable` | Vanligt ifyllbart formulär. Utseenden är förgenererade så alla läsare visar värdena direkt. |

### Inställningar

| Medlem | Beskrivning |
|---|---|
| `bool ThrowOnUnsupportedCharacters` | `true` ger `PdfFormException` när ett värde innehåller tecken som inte kan ritas. Standard `false` ritar `?`. Fältets lagrade värde bevaras alltid. |
| `static long MaxDecodedStreamBytes` | Övre gräns per uppackad ström, standard 256 MB. Skyddar mot dekomprimeringsbomber i opålitliga filer. |

### Felhantering

- Alla fel från biblioteket är `PdfFormException` (oläsbar fil, lösenordskrav, fel fälttyp, okänt alternativ) eller dess
  underklass `FieldNotFoundException` med egenskapen `FieldName`.
- Fel som uppstår inne i biblioteket under `Save`, till exempel en skadad fil som först märks vid skrivning, ges också som
  `PdfFormException`, med det ursprungliga undantaget i `InnerException`. Samma gäller för fel vid öppning.
- Felaktiga argument ger `ArgumentNullException` eller `ArgumentOutOfRangeException`. Använt efter `Dispose` ger `ObjectDisposedException`.
- Sidoperationer och `Append` validerar allt innan något ändras. Misslyckas anropet är dokumentet orört.
- Ett prefix kan bara användas en gång per dokument, och får inte krocka med ett befintligt fältnamn.
- Orörda fält behåller mallens eget utseende. Bara fält du ändrar, eller fält som saknar utseende i mallen, ritas om.
- En instans är inte trådsäker. Använd en instans per tråd eller anrop.

## Widget-API för äldre kod

Paketet **PdfFormFiller.Widgets** erbjuder ett widget-baserat API i namnrymden `PdfFormFiller.Widgets`. Det är till
för befintlig kod som arbetar med fältobjekt som castas till typ, till exempel `PdfTextBoxFieldWidget`. Klass- och
medlemsnamnen är de invanda, så en migrering består av att byta using-satserna mot en enda:

```csharp
using PdfFormFiller.Widgets;
```

```csharp
PdfDocument doc = new PdfDocument();
doc.LoadFromFile("mall.pdf");
PdfFormWidget form = doc.Form as PdfFormWidget;
foreach (PdfField field in form.FieldsWidget.List)
{
    if (field is PdfTextBoxFieldWidget t && t.Name == "namn") t.Text = "Åsa";
    if (field is PdfCheckBoxWidgetFieldWidget c) c.Checked = true;
    if (field is PdfRadioButtonListFieldWidget r) r.SelectedIndex = 1;
    if (field is PdfComboBoxWidgetFieldWidget cb) cb.SelectedIndex = 2;
}
form.IsFlatten = true;
doc.SaveToStream(ms, FileFormat.PDF);
```

Täckt: `PdfDocument` (konstruktorer, `LoadFromFile/Bytes/Stream`, `Form`, `Pages` med `Count/RemoveAt/Remove`,
`AppendPage`, `InsertPage` med och utan målposition, `InsertPageRange`, `MergeFiles`, `SaveToFile`, `SaveToStream`, `Close`), `PdfFormWidget`
(`Fields`, `FieldsWidget`, `IsFlatten`, `ReadOnly`, `NeedAppearance`) och dess fältsamling (`Count`, indexering på index och namn, `List`),
`PdfField` (`Name`, `FullName`, `ReadOnly`, `Required`, `Flatten`, `Export`), `PdfTextBoxFieldWidget.Text` och
`TextAlignment` (`PdfTextAlignment`),
`PdfCheckBoxWidgetFieldWidget.Checked`, `PdfRadioButtonListFieldWidget.SelectedIndex/SelectedValue/Items`,
`PdfComboBoxWidgetFieldWidget` och `PdfListBoxWidgetFieldWidget` med `SelectedIndex/SelectedValue/Items`.

Lösenord finns som överlagringar: `PdfDocument(filename|bytes|stream, string? password)` och
`LoadFromFile/LoadFromBytes/LoadFromStream(..., string? password)`, med samma regler som `PdfFormDocument.Load`. Ett misslyckat
anrop lämnar det aktuella dokumentet orört.

Skillnader från de äldre klasserna i detta lager:

- `Items[i].Text` är visningstexten och `Items[i].Value` exportvärdet, det som lagras i fältet.
- `SelectedIndex` kastar `ArgumentOutOfRangeException` utanför `-1..Count-1`. `-1` rensar valet.
- `PdfPageBase.Index` följer sin sida. Efter att sidan tagits bort är värdet `-1`.
- `LoadFrom*` på samma instans behåller `Form`, men `IsFlatten` och `ReadOnly` nollställs, och gamla fält- och sidobjekt slutar fungera.
- Fältens `Flatten`-flaggor följer inte med vid `AppendPage` eller `InsertPage`. De är en inställning för sparning och
  tillämpas på nytt vid varje sparning.

Lagret täcker bara formulär. Rendering, textextraktion och att rita på sidor finns inte. Saknas en medlem ni använder
blir det ett kompileringsfel, inte ett tyst fel, så det syns direkt vid bytet.

## Exempel: sammanfoga, klipp och lås

```csharp
using var doc = PdfFormDocument.Load(huvud);
doc.Append(bilaga, fieldPrefix: "bilaga")          // bilagans fält heter nu "bilaga.xxx"
   .Append(villkor, pageIndices: new[] { 0, 2 })   // bara sida 1 och 3 ur villkor.pdf
   .RemovePage(1)                                  // ta bort sida 2 ur resultatet
   .SetText("namn", "Kalle")
   .SetText("bilaga.namn", "Kalle")
   .Save("klar.pdf", OutputMode.Flattened);
```

## Begränsningar

- Fält du ändrar ritas med Helvetica (eller Liberation Sans för tecken utanför WinAnsi). Fontval i mallens `/DA` (t.ex. fet stil) ignoreras för dessa, men storlek, färg och justering respekteras. Storlek `0` ger automatisk storlek.
- Kombfält ritas med ett tecken per ruta. Rich text ritas som vanlig text.
- Tecken som saknas i båda fonterna (t.ex. kinesiska, emoji) blir `?`.
- Formateringsskript i mallen (datum- och talformat via JavaScript) körs inte. Värdet ritas som du skickar in det.
- Signaturfält lämnas orörda och ritas med sin befintliga appearance vid flattening.
- PDF:er med användarlösenord stöds när lösenordet anges vid `Load`. Ägarlösenord (skydd mot redigering/utskrift) är inget hinder.
- Certifikatskyddade PDF:er (`Adobe.PubSec`) stöds inte.
- Namngivna `/Crypt`-filter på enskilda strömmar tolkas inte: sådana strömmar dekrypteras inte. `/EFF` läses inte.
- `MaxLen` tillämpas inte på värdet. Kammfält (comb) ritar bara de första `MaxLen` tecknen, medan det lagrade värdet
  behåller hela texten.
- Fältträd djupare än 256 nivåer läses inte.
- I widget-API:t kan `SelectedIndex` välja fel alternativ när exportvärdena är dubbletter eller tomma, eftersom
  valet görs via exportvärdet.

## Bygga

```bash
dotnet build -c Release
dotnet pack src/PdfFormFiller/PdfFormFiller.csproj -c Release -o nupkg
dotnet pack src/PdfFormFiller.Widgets/PdfFormFiller.Widgets.csproj -c Release -o nupkg
```

Kräver .NET 8 SDK. Inga andra verktyg eller paket behövs.

## Installera

Paketen publiceras till GitHub Packages. Lägg till källan en gång, i Visual Studio under Tools, Options,
NuGet Package Manager, Package Sources, eller från kommandoraden:

```bash
dotnet nuget add source https://nuget.pkg.github.com/Haansen/index.json --name Haansen --username DITT_GITHUB_NAMN --password DIN_TOKEN --store-password-in-clear-text
dotnet add package PdfFormFiller
```

GitHub Packages kräver inloggning även för publika paket. Använd en personlig åtkomsttoken med rättigheten
`read:packages`. Vill du slippa det kan du ladda ner `.nupkg`-filerna från repots Releases-sida och lägga dem i en
lokal mapp som du pekar ut som paketkälla.

## Släppa en ny version

GitHub Actions bygger och publicerar paketen när en versionstagg pushas. Taggen bestämmer versionsnumret:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Paketen hamnar i GitHub Packages och bifogas en release. Finns repo-hemligheten `NUGET_API_KEY` publiceras de även
till nuget.org.

## Licens

PdfFormFiller är licensierat under MIT-licensen, se filen `LICENSE`. Du får använda, ändra och sprida biblioteket fritt,
även kommersiellt, så länge upphovsrätts- och licenstexten följer med.

Fonten Liberation Sans i `src/PdfFormFiller/Fonts` används för textmätning och bäddas in i PDF:er som innehåller tecken
utanför WinAnsi. Den omfattas av SIL Open Font License, se licensfilen i samma mapp.
