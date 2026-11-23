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
- Kryptering: standardhanteraren med RC4 40/128, AES-128 och AES-256 (R2 till R6) när dokumentet öppnas utan användarlösenord, dvs. "ägarlösenord"-skydd. Utdata skrivs alltid okrypterat
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
| `PdfFormDocument.Merge(IEnumerable<byte[]> docs, bool prefixFieldsWithIndex = false)` | Slå ihop flera PDF:er. Med prefix får fälten namn `0.namn`, `1.namn` osv. |

### Läsa

| Medlem | Beskrivning |
|---|---|
| `int PageCount` | Antal sidor |
| `IReadOnlyList<FormFieldInfo> GetFields()` | Alla fält: `Name`, `Type`, `Value`, `Options`, `Pages` (0-baserat), `ReadOnly`, `Required`, `MultiLine` |
| `string? GetValue(string fieldName)` | Aktuellt värde |
| `bool HasField(string fieldName)` | Finns fältet |

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
| `RenameField(string name, string newName)` | Byter namn. Vid punktnotation (`grupp.falt`) byts sista delen. |

Fel: `FieldNotFoundException` (ärver `PdfFormException`) när fältet saknas, `PdfFormException` vid fel typ eller okänt alternativ.
Felmeddelandet listar tillgängliga alternativ.

### Sidor och sammanfogning

| Metod | Beskrivning |
|---|---|
| `RemovePage(int index)` | Tar bort en sida (0-baserat). Fält som bara fanns på sidan försvinner. Sista sidan kan inte tas bort. |
| `RemovePages(params int[] indices)` | Flera sidor |
| `KeepPages(params int[] indices)` | Behåll bara angivna sidor |
| `Append(byte[] pdf, int[]? pageIndices = null, string? fieldPrefix = null)` | Lägg till sidor från en annan PDF, alla eller utvalda. Med `fieldPrefix` får fälten namn `prefix.namn`. Utan prefix får krockande namn suffix `_2`, `_3`. |
| `Append(PdfFormDocument other, ...)` | Samma, från ett annat laddat dokument |
| `Insert(byte[] pdf, int atPageIndex, int[]? pageIndices = null, string? fieldPrefix = null)` | Som `Append`, men sidorna hamnar på angiven position |
| `MovePage(int fromIndex, int toIndex)` | Flyttar en sida |

### Spara

| Metod | Beskrivning |
|---|---|
| `byte[] Save(OutputMode mode = OutputMode.Flattened)` | Färdig PDF som bytes. Dokumentet kan användas vidare efteråt. |
| `void Save(string path, OutputMode mode = Flattened)` | Till fil |
| `void Save(Stream s, OutputMode mode = Flattened)` | Till ström |

`OutputMode`:

| Läge | Resultat |
|---|---|
| `Flattened` | Fältens utseende ritas in i sidinnehållet, alla formulärfält tas bort. Standard. |
| `ReadOnly` | Fälten finns kvar med värden men är flaggade read-only. Värdena kan läsas ut maskinellt senare. |
| `Editable` | Vanligt ifyllbart formulär. Utseenden är förgenererade så alla läsare visar värdena direkt. |

| `SetTextAlignment(string name, TextAlignment a)` | Vänster, centrerat eller höger för text- och valfält. `GetTextAlignment` läser aktuellt värde. |
| `SetReadOnly(string name, bool readOnly = true)` | Låser ett enskilt fält. Gäller i lägena Editable och ReadOnly. |
| `FlattenFields(params string[] names)` | Plattar bara angivna fält vid sparning i Editable eller ReadOnly. Övriga fält förblir levande. |
| `ClearFlattenFields()` | Ångrar tidigare `FlattenFields`. |
| `PrefixFields(string prefix)` | Lägger alla fält under ett prefix (`namn` blir `prefix.namn`). Används före `Append` av samma mall igen. |

### Inställningar

| Medlem | Beskrivning |
|---|---|
| `bool ThrowOnUnsupportedCharacters` | `true` ger `PdfFormException` när ett värde innehåller tecken som inte kan ritas. Standard `false` ritar `?`. Fältets lagrade värde bevaras alltid. |
| `static long MaxDecodedStreamBytes` | Övre gräns per uppackad ström, standard 256 MB. Skyddar mot dekomprimeringsbomber i opålitliga filer. |

### Felhantering

- Alla fel från biblioteket är `PdfFormException` (oläsbar fil, lösenordskrav, fel fälttyp, okänt alternativ) eller dess
  underklass `FieldNotFoundException` med egenskapen `FieldName`.
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
(`Fields`, `FieldsWidget`, indexering på index och namn, `List`, `IsFlatten`, `ReadOnly`, `NeedAppearance`),
`PdfField` (`Name`, `FullName`, `ReadOnly`, `Required`, `Flatten`, `Export`), `PdfTextBoxFieldWidget.Text` och
`TextAlignment` (`PdfTextAlignment`),
`PdfCheckBoxWidgetFieldWidget.Checked`, `PdfRadioButtonListFieldWidget.SelectedIndex/SelectedValue/Items`,
`PdfComboBoxWidgetFieldWidget` och `PdfListBoxWidgetFieldWidget` med `SelectedIndex/SelectedValue/Items`.

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
- PDF:er som kräver ett användarlösenord för att öppnas stöds inte. Ägarlösenord (skydd mot redigering/utskrift) är inget hinder.
- Publika nyckel-krypterade PDF:er (certifikat) stöds inte.

## Bygga

```bash
dotnet build -c Release
dotnet pack src/PdfFormFiller/PdfFormFiller.csproj -c Release -o nupkg
dotnet pack src/PdfFormFiller.Widgets/PdfFormFiller.Widgets.csproj -c Release -o nupkg
```

Kräver .NET 8 SDK. Inga andra verktyg eller paket behövs.

## Släppa en ny version

Paketen publiceras till nuget.org av GitHub Actions när en versionstagg pushas. Taggen bestämmer versionsnumret:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Flödet kräver repo-hemligheten `NUGET_API_KEY` med en API-nyckel från nuget.org.

## Licenser

Fonten Liberation Sans i `src/PdfFormFiller/Fonts` används för textmätning och bäddas in i PDF:er som innehåller tecken
utanför WinAnsi. Den omfattas av SIL Open Font License, se licensfilen i samma mapp.
