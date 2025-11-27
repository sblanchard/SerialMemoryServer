using SerialMemory.Core.Interfaces;
using SerialMemory.Core.Models;
using System.Text.RegularExpressions;

namespace SerialMemory.ML;

/// <summary>
/// Software-aware entity extraction service.
/// Extends pattern-based extraction with software engineering entities.
/// </summary>
public partial class SoftwareEntityExtractionService : IEntityExtractionService
{
    // =========================================================================
    // GENERIC ENTITY PATTERNS (from PatternEntityExtractionService)
    // =========================================================================

    [GeneratedRegex(@"\b[A-Z][a-z]+ [A-Z][a-z]+\b", RegexOptions.Compiled)]
    private static partial Regex PersonNameRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?: [A-Z][a-z]+)* (?:Inc|Corp|LLC|Ltd|Company|Corporation)\b", RegexOptions.Compiled)]
    private static partial Regex OrganizationRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?: [A-Z][a-z]+)*,? [A-Z]{2}\b", RegexOptions.Compiled)]
    private static partial Regex LocationRegex();

    [GeneratedRegex(@"\b\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    // =========================================================================
    // SOFTWARE ENTITY PATTERNS
    // =========================================================================

    // GitHub/GitLab repo patterns (org/repo format)
    [GeneratedRegex(@"\b(?:github\.com|gitlab\.com|bitbucket\.org)[/:]([a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RepoUrlRegex();

    [GeneratedRegex(@"\b([a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+)(?:\s+(?:repo|repository))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RepoMentionRegex();

    // Service/API names (PascalCase or kebab-case with -service, -api suffix)
    [GeneratedRegex(@"\b([A-Z][a-zA-Z0-9]*(?:Service|API|Api|Worker|Handler|Controller))\b", RegexOptions.Compiled)]
    private static partial Regex ServiceNamePascalRegex();

    [GeneratedRegex(@"\b([a-z][a-z0-9]*(?:-[a-z0-9]+)*-(?:service|api|worker|handler))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ServiceNameKebabRegex();

    // Database names (PostgreSQL, MySQL, MongoDB, Redis, etc.)
    [GeneratedRegex(@"\b((?:postgresql?|mysql|mongodb|redis|elasticsearch|cassandra|dynamodb|cosmosdb|firestore|supabase|planetscale|neon|cockroach(?:db)?|timescale(?:db)?|influx(?:db)?)\s*(?:cluster|database|db|instance)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseTypeRegex();

    [GeneratedRegex(@"\b([a-z][a-z0-9_]*(?:_db|_database|_store|_cache))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseNameRegex();

    // Table names (snake_case, typically after "table" or in SQL context)
    [GeneratedRegex(@"\b(?:table|from|join|into)\s+([a-z][a-z0-9_]*)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TableNameRegex();

    // Technology/framework names
    [GeneratedRegex(@"\b(\.NET|ASP\.NET|React|Vue|Angular|Next\.js|Nuxt|Svelte|Node\.js|Express|FastAPI|Django|Flask|Spring(?:\s*Boot)?|Rails|Laravel|Phoenix|NestJS|Deno|Bun|Astro|Remix|Qwik|SolidJS|Blazor|MAUI|WPF|WinForms|Electron|Tauri|Flutter|React\s*Native|Expo|Capacitor|Ionic|Kotlin|Swift|Rust|Go(?:lang)?|Python|TypeScript|JavaScript|C#|F#|Java|Scala|Clojure|Elixir|Erlang|Haskell|OCaml|Ruby|PHP|Perl|Lua|Zig|Nim|Crystal|Julia|R|MATLAB|Dart|Objective-C)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TechnologyRegex();

    // Version patterns (v1.2.3, version 1.2.3, release 1.2.3)
    [GeneratedRegex(@"\b(?:v(?:ersion)?|release)\s*(\d+(?:\.\d+)*(?:-[a-zA-Z0-9.]+)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\bv(\d+(?:\.\d+)*(?:-[a-zA-Z0-9.]+)?)\b", RegexOptions.Compiled)]
    private static partial Regex SemverRegex();

    // Environment names
    [GeneratedRegex(@"\b((?:dev(?:elopment)?|staging|stg|uat|qa|test(?:ing)?|prod(?:uction)?|local|sandbox|preview|canary|beta|alpha)\s*(?:env(?:ironment)?|server|cluster)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentRegex();

    // API endpoint patterns
    [GeneratedRegex(@"\b((?:GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+/[a-zA-Z0-9/_\-{}:]+)\b", RegexOptions.Compiled)]
    private static partial Regex ApiEndpointRegex();

    [GeneratedRegex(@"\b(/(?:api|v\d+)/[a-zA-Z0-9/_\-{}:]+)\b", RegexOptions.Compiled)]
    private static partial Regex ApiPathRegex();

    // Message queue/event names
    [GeneratedRegex(@"\b([a-z][a-z0-9._-]*(?:\.(?:created|updated|deleted|processed|failed|completed|started|finished|event|message|command|query))+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EventNameRegex();

    [GeneratedRegex(@"\b([a-zA-Z][a-zA-Z0-9]*(?:Event|Message|Command|Query|Request|Response))\b", RegexOptions.Compiled)]
    private static partial Regex MessageTypeRegex();

    // Config file patterns
    [GeneratedRegex(@"\b([a-z][a-z0-9._-]*\.(?:json|yaml|yml|toml|env|config|conf|ini|xml|properties))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ConfigFileRegex();

    // Module/package names (npm, NuGet, PyPI patterns)
    [GeneratedRegex(@"\b(?:npm|yarn|pnpm)\s+(?:install|add|i)\s+([a-z@][a-z0-9@/_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NpmPackageRegex();

    [GeneratedRegex(@"\b(?:dotnet\s+add\s+package|Install-Package|PackageReference)\s+([A-Za-z][A-Za-z0-9._]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NugetPackageRegex();

    [GeneratedRegex(@"\b(?:pip\s+install|poetry\s+add)\s+([a-z][a-z0-9_-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PypiPackageRegex();

    // Bug/issue references
    [GeneratedRegex(@"\b(?:bug|issue|ticket|jira|fix(?:es)?|closes?|resolves?)\s*[#:]?\s*([A-Z]{2,10}-\d+|\d{4,})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BugReferenceRegex();

    // Test names
    [GeneratedRegex(@"\b([A-Z][a-zA-Z0-9]*(?:Test|Tests|Spec|Specs))\b", RegexOptions.Compiled)]
    private static partial Regex TestClassRegex();

    [GeneratedRegex(@"\b(?:test|it|describe|should)\s*[([\x22']([^)\x22']+)[)\x22']\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TestNameRegex();

    // Deployment/infrastructure
    [GeneratedRegex(@"\b((?:kubernetes|k8s|docker|helm|terraform|pulumi|cloudformation|aws|azure|gcp|heroku|vercel|netlify|railway|render|fly\.io)\s*(?:cluster|deployment|stack|infra(?:structure)?)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex InfrastructureRegex();

    // Incident references
    [GeneratedRegex(@"\b(?:incident|outage|pager|alert|sev-?\d|p\d)\s*[#:]?\s*([A-Z]{0,5}\d{4,}|[A-Z]{2,10}-\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex IncidentReferenceRegex();

    // Team names
    [GeneratedRegex(@"\b([A-Z][a-zA-Z0-9]*\s*(?:Team|Squad|Guild|Tribe|Chapter))\b", RegexOptions.Compiled)]
    private static partial Regex TeamNameRegex();

    // =========================================================================
    // HARDWARE/ENGINEERING ENTITY PATTERNS
    // =========================================================================

    // Part numbers (common IC/component patterns like LM358, ESP32-WROOM-32, STM32F407VGT6)
    [GeneratedRegex(@"\b([A-Z]{2,4}\d{2,5}[A-Z0-9\-]{0,15})\b", RegexOptions.Compiled)]
    private static partial Regex PartNumberRegex();

    // Specific manufacturer part prefixes (more precise matching)
    [GeneratedRegex(@"\b((?:LM|TL|OP|AD|TI|MAX|MCP|PIC|ATmega|ATtiny|STM32|ESP32|ESP8266|nRF52|CC2[56]\d{2}|RP2\d{3}|SAMD\d{2}|EFM32|LPC\d{4}|XMC\d{4}|CY\d{4}|AM\d{4}|i\.MX\d{3}|BCM\d{4}|MT\d{4})[A-Z0-9\-]*)\b", RegexOptions.Compiled)]
    private static partial Regex ManufacturerPartRegex();

    // Voltage patterns (3.3V, 5V, 12V, 1.8V, etc.)
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*[Vv](?:DC|AC|olt(?:s)?|RMS|pp|p-p)?\b", RegexOptions.Compiled)]
    private static partial Regex VoltageRegex();

    // Current patterns (100mA, 2A, 500μA, etc.)
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*([mμunp]?[Aa](?:mp(?:s|ere(?:s)?)?)?)\b", RegexOptions.Compiled)]
    private static partial Regex CurrentRegex();

    // Frequency patterns (16MHz, 2.4GHz, 50Hz, 100kHz, etc.)
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*([kKMGT]?[Hh]z)\b", RegexOptions.Compiled)]
    private static partial Regex FrequencyRegex();

    // Temperature patterns (25°C, -40 to 85°C, 125 degrees, etc.)
    [GeneratedRegex(@"\b(-?\d+(?:\.\d+)?)\s*(?:°|deg(?:rees?)?)\s*([CF])\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TemperatureRegex();

    // Temperature range patterns (-40°C to 125°C)
    [GeneratedRegex(@"\b(-?\d+)\s*(?:°|deg)?\s*([CF])?\s*(?:to|-)\s*(-?\d+)\s*(?:°|deg)?\s*([CF])\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TemperatureRangeRegex();

    // Dimension patterns (10mm, 2.54mm, 0.1", etc.)
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*(mm|cm|m|in|inch(?:es)?|mil|μm|um)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DimensionRegex();

    // Power patterns (10W, 0.25W, 100mW, etc.)
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*([mμnpkKM]?[Ww](?:att(?:s)?)?)\b", RegexOptions.Compiled)]
    private static partial Regex PowerRegex();

    // Communication protocols
    [GeneratedRegex(@"\b(I2C|I²C|SPI|UART|USART|RS-?232|RS-?485|RS-?422|CAN(?:\s*bus)?|LIN|JTAG|SWD|SDIO|QSPI|USB|Ethernet|WiFi|Wi-Fi|Bluetooth|BLE|Zigbee|Z-Wave|LoRa|NFC|RFID|HDMI|DisplayPort|MIPI|LVDS|GPIO)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ProtocolRegex();

    // Component types
    [GeneratedRegex(@"\b(resistor|capacitor|inductor|diode|LED|transistor|MOSFET|BJT|op[\-\s]?amp|operational\s+amplifier|regulator|LDO|buck|boost|SMPS|transformer|relay|crystal|oscillator|connector|header|fuse|varistor|thermistor|potentiometer|switch|button|sensor|ADC|DAC|MCU|microcontroller|FPGA|CPLD|ASIC|DSP|memory|EEPROM|flash|SRAM|DRAM|ROM)s?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ComponentTypeRegex();

    // Sensor types
    [GeneratedRegex(@"\b((?:temperature|pressure|humidity|accelerometer|gyroscope|IMU|magnetometer|proximity|distance|ultrasonic|infrared|IR|PIR|motion|light|ambient|UV|gas|CO2|smoke|flame|water|moisture|soil|pH|flow|level|load|strain|force|torque|current|voltage|power|hall\s*effect|encoder|GPS|GNSS)\s*(?:sensor|detector|transducer|meter)?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SensorTypeRegex();

    // Actuator types
    [GeneratedRegex(@"\b((?:servo|stepper|DC|brushless|BLDC|motor|pump|valve|solenoid|speaker|buzzer|heater|fan|relay|SSR|linear\s*actuator|piezo)(?:\s*(?:motor|driver|actuator|element))?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ActuatorTypeRegex();

    // PCB-related terms
    [GeneratedRegex(@"\b(PCB|printed\s*circuit\s*board|schematic|BOM|bill\s*of\s*materials|Gerber|drill\s*file|pick\s*and\s*place|assembly\s*drawing|silkscreen|solder\s*mask|copper\s*pour|trace|via|pad|footprint|land\s*pattern)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PcbTermRegex();

    // Engineering standards
    [GeneratedRegex(@"\b(IPC-\d{4}[A-Z]?|J-STD-\d{3}[A-Z]?|MIL-STD-\d{3,5}[A-Z]?|ISO\s*\d{4,5}|IEC\s*\d{4,5}|IEEE\s*\d{3,4}|UL\s*\d{3,4}|CE|RoHS|REACH|FCC(?:\s*Part\s*\d+)?|EN\s*\d{4,5}|JEDEC|AEC-Q\d{3})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex StandardRegex();

    // Major electronics manufacturers
    [GeneratedRegex(@"\b(Texas\s*Instruments|TI|STMicroelectronics|ST|NXP|Microchip|Analog\s*Devices|Maxim|ON\s*Semiconductor|Infineon|Renesas|Nordic\s*Semiconductor|Espressif|Raspberry\s*Pi|Arduino|Adafruit|SparkFun|Digikey|Mouser|LCSC|JLCPCB|PCBWay|Murata|TDK|Samsung|Vishay|Bourns|TE\s*Connectivity|Amphenol)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ManufacturerRegex();

    // Pin/signal names
    [GeneratedRegex(@"\b(VCC|VDD|VSS|GND|VBAT|VREF|AVCC|AVDD|DGND|AGND|EN|RST|RESET|CLK|SCK|SCLK|MOSI|MISO|CS|SS|NSS|SDA|SCL|TX|RX|TXD|RXD|CTS|RTS|DTR|DSR|INT|IRQ|PWM|ADC\d?|DAC\d?|GPIO\d{1,2}|PA\d{1,2}|PB\d{1,2}|PC\d{1,2}|PD\d{1,2})\b", RegexOptions.Compiled)]
    private static partial Regex PinNameRegex();

    // Connector types
    [GeneratedRegex(@"\b(USB(?:-[ABC])?|micro-?USB|USB-C|Type-C|barrel\s*jack|DC\s*jack|JST(?:-[A-Z]{2,3})?|Molex|D-sub|DB-?\d{1,2}|RJ-?\d{1,2}|SMA|BNC|header|dupont|screw\s*terminal|terminal\s*block|banana\s*plug|binding\s*post)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectorTypeRegex();

    // =========================================================================
    // HARDWARE/ENGINEERING RELATIONSHIP PATTERNS
    // =========================================================================

    // X connects to Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:connects?\s+to|connected\s+to|wired\s+to|linked\s+to)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectsToRegex();

    // X mounted on Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:mounted\s+on|attached\s+to|soldered\s+to|placed\s+on)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MountedOnRegex();

    // X powers Y / X powered by Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:powers?|supplies|provides\s+power\s+to)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PowersRegex();

    // X controls Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:controls?|drives?|actuates?|switches?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ControlsRegex();

    // X measures Y / X reads Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:measures?|reads?|senses?|monitors?|detects?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MeasuresRegex();

    // X communicates via Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:communicates?\s+via|uses?|connects?\s+via)\s+(I2C|SPI|UART|CAN|USB|Ethernet|WiFi|Bluetooth|RS-?(?:232|485))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CommunicatesViaRegex();

    // X rated for Y (specifications)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:rated\s+(?:for|at)|supports?\s+up\s+to)\s+(\d+(?:\.\d+)?(?:\s*[VAmWHz]|\s*°[CF])+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RatedForRegex();

    // X complies with Y (standards)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:complies?\s+with|meets?|certified\s+(?:to|for)|conforms?\s+to)\s+([A-Za-z0-9\-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CompliesWithRegex();

    // X manufactured by Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:manufactured\s+by|made\s+by|from|by)\s+(Texas\s*Instruments|TI|STMicroelectronics|ST|NXP|Microchip|Analog\s*Devices|Maxim|ON\s*Semiconductor|Infineon|Renesas|Nordic|Espressif)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ManufacturedByRegex();

    // X part of Y (assembly)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:is\s+)?(?:part\s+of|component\s+of|included\s+in|belongs\s+to)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PartOfRegex();

    // X interfaces with Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:interfaces?\s+with|interacts?\s+with|talks?\s+to)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex InterfacesWithRegex();

    // =========================================================================
    // SOFTWARE RELATIONSHIP PATTERNS
    // =========================================================================

    // X depends on Y / X uses Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:depends?\s+on|uses|requires|imports?|needs?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DependsOnRegex();

    // X calls Y / X invokes Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:calls?|invokes?|requests?|queries|fetches\s+from)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CallsRegex();

    // X deployed to Y / X runs on Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:deployed\s+(?:to|on)|runs?\s+on|hosted\s+(?:on|by))\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DeployedToRegex();

    // X owns Y / X maintains Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_\s.-]+)\s+(?:owns?|maintains?|manages?|is\s+responsible\s+for)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex OwnsRegex();

    // X implements Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:implements?|exposes?|provides?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ImplementsRegex();

    // X emits/publishes Y (events)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:emits?|publishes?|produces?|sends?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EmitsRegex();

    // X consumes/subscribes Y (events)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:consumes?|subscribes?\s+to|listens?\s+(?:to|for)|handles?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ConsumesRegex();

    // X fixes/caused Y (bugs/incidents)
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:fixes?|fixed|resolves?|resolved)\s+([A-Za-z0-9_#.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FixesRegex();

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:caused?|triggered?|introduced?|broke)\s+([A-Za-z0-9_#.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CausedRegex();

    // X tests Y
    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9_.-]+)\s+(?:tests?|validates?|verifies?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TestsRegex();

    // X works on Y (person -> project/service)
    [GeneratedRegex(@"([A-Z][a-z]+ [A-Z][a-z]+)\s+(?:works?\s+on|developing|built|created|maintains?)\s+([A-Za-z][A-Za-z0-9_.-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex WorksOnRegex();

    public Task<List<ExtractedEntity>> ExtractEntitiesAsync(string text, CancellationToken cancellationToken = default)
    {
        var entities = new List<ExtractedEntity>();

        // =====================================================================
        // SOFTWARE ENTITIES (higher priority)
        // =====================================================================

        // Repositories
        foreach (Match match in RepoUrlRegex().Matches(text))
        {
            entities.Add(new ExtractedEntity(match.Groups[1].Value, "REPO", match.Index, match.Index + match.Length, 0.95f));
        }
        foreach (Match match in RepoMentionRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "REPO", match.Index, match.Index + match.Length, 0.8f));
        }

        // Services/APIs (PascalCase)
        foreach (Match match in ServiceNamePascalRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "SERVICE", match.Index, match.Index + match.Length, 0.85f));
        }

        // Services/APIs (kebab-case)
        foreach (Match match in ServiceNameKebabRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "SERVICE", match.Index, match.Index + match.Length, 0.85f));
        }

        // API endpoints
        foreach (Match match in ApiEndpointRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "API", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in ApiPathRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "API", match.Index, match.Index + match.Length, 0.85f));
        }

        // Databases
        foreach (Match match in DatabaseTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "DATABASE", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in DatabaseNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "DATABASE", match.Index, match.Index + match.Length, 0.75f));
        }

        // Tables
        foreach (Match match in TableNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TABLE", match.Index, match.Index + match.Length, 0.85f));
        }

        // Technologies/frameworks
        foreach (Match match in TechnologyRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TECH", match.Index, match.Index + match.Length, 0.95f));
        }

        // Versions
        foreach (Match match in VersionRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "VERSION", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in SemverRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "VERSION", match.Index, match.Index + match.Length, 0.85f));
        }

        // Environments
        foreach (Match match in EnvironmentRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "ENV", match.Index, match.Index + match.Length, 0.9f));
        }

        // Events/messages
        foreach (Match match in EventNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MESSAGE", match.Index, match.Index + match.Length, 0.85f));
        }
        foreach (Match match in MessageTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MESSAGE", match.Index, match.Index + match.Length, 0.8f));
        }

        // Config files
        foreach (Match match in ConfigFileRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "CONFIG", match.Index, match.Index + match.Length, 0.9f));
        }

        // Modules/packages
        foreach (Match match in NpmPackageRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MODULE", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in NugetPackageRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MODULE", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in PypiPackageRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "MODULE", match.Index, match.Index + match.Length, 0.9f));
        }

        // Bugs/issues
        foreach (Match match in BugReferenceRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "BUG", match.Index, match.Index + match.Length, 0.9f));
        }

        // Tests
        foreach (Match match in TestClassRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TEST", match.Index, match.Index + match.Length, 0.85f));
        }
        foreach (Match match in TestNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TEST", match.Index, match.Index + match.Length, 0.8f));
        }

        // Infrastructure/deployment
        foreach (Match match in InfrastructureRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "DEPLOYMENT", match.Index, match.Index + match.Length, 0.85f));
        }

        // Incidents
        foreach (Match match in IncidentReferenceRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "INCIDENT", match.Index, match.Index + match.Length, 0.9f));
        }

        // Teams
        foreach (Match match in TeamNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "TEAM", match.Index, match.Index + match.Length, 0.85f));
        }

        // =====================================================================
        // HARDWARE/ENGINEERING ENTITIES
        // =====================================================================

        // Manufacturer part numbers (high confidence - specific prefixes)
        foreach (Match match in ManufacturerPartRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "PART_NUMBER", match.Index, match.Index + match.Length, 0.95f));
        }

        // Generic part numbers (lower confidence)
        foreach (Match match in PartNumberRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "PART_NUMBER", match.Index, match.Index + match.Length, 0.7f));
        }

        // Communication protocols
        foreach (Match match in ProtocolRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value.ToUpperInvariant(), "PROTOCOL", match.Index, match.Index + match.Length, 0.95f));
        }

        // Voltages
        foreach (Match match in VoltageRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value.Trim(), "VOLTAGE", match.Index, match.Index + match.Length, 0.9f));
        }

        // Currents
        foreach (Match match in CurrentRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value.Trim(), "CURRENT", match.Index, match.Index + match.Length, 0.9f));
        }

        // Frequencies
        foreach (Match match in FrequencyRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value.Trim(), "FREQUENCY", match.Index, match.Index + match.Length, 0.9f));
        }

        // Temperatures
        foreach (Match match in TemperatureRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value.Trim(), "TEMPERATURE", match.Index, match.Index + match.Length, 0.9f));
        }
        foreach (Match match in TemperatureRangeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value.Trim(), "TEMPERATURE", match.Index, match.Index + match.Length, 0.85f));
        }

        // Dimensions
        foreach (Match match in DimensionRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value.Trim(), "DIMENSION", match.Index, match.Index + match.Length, 0.85f));
        }

        // Component types
        foreach (Match match in ComponentTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "COMPONENT", match.Index, match.Index + match.Length, 0.9f));
        }

        // Sensor types
        foreach (Match match in SensorTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "SENSOR", match.Index, match.Index + match.Length, 0.9f));
        }

        // Actuator types
        foreach (Match match in ActuatorTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "ACTUATOR", match.Index, match.Index + match.Length, 0.9f));
        }

        // PCB terms
        foreach (Match match in PcbTermRegex().Matches(text))
        {
            var term = match.Groups[1].Value.ToUpperInvariant();
            var entityType = term switch
            {
                "PCB" or "PRINTED CIRCUIT BOARD" => "PCB",
                "SCHEMATIC" => "SCHEMATIC",
                "BOM" or "BILL OF MATERIALS" => "BOM",
                _ => "PCB" // Default to PCB for other terms like Gerber, trace, via, etc.
            };
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), entityType, match.Index, match.Index + match.Length, 0.9f));
        }

        // Engineering standards
        foreach (Match match in StandardRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value.ToUpperInvariant(), "STANDARD", match.Index, match.Index + match.Length, 0.95f));
        }

        // Manufacturers
        foreach (Match match in ManufacturerRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "MANUFACTURER", match.Index, match.Index + match.Length, 0.9f));
        }

        // Pin/signal names
        foreach (Match match in PinNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Groups[1].Value, "PIN", match.Index, match.Index + match.Length, 0.85f));
        }

        // Connector types
        foreach (Match match in ConnectorTypeRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(SoftwareGraphTypes.NormalizeEntityName(match.Groups[1].Value), "CONNECTOR", match.Index, match.Index + match.Length, 0.9f));
        }

        // =====================================================================
        // GENERIC ENTITIES (lower priority, avoid overlap with software entities)
        // =====================================================================

        // Emails
        foreach (Match match in EmailRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value, "PERSON", match.Index, match.Index + match.Length, 0.7f));
        }

        // Organizations
        foreach (Match match in OrganizationRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value, "ORG", match.Index, match.Index + match.Length, 0.8f));
        }

        // Person names (be more conservative to avoid matching service names)
        foreach (Match match in PersonNameRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
            {
                // Skip if it looks like a service name
                var value = match.Value;
                if (!value.EndsWith("Service") && !value.EndsWith("Api") && !value.EndsWith("Handler"))
                {
                    entities.Add(new ExtractedEntity(value, "PERSON", match.Index, match.Index + match.Length, 0.6f));
                }
            }
        }

        // Locations
        foreach (Match match in LocationRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
                entities.Add(new ExtractedEntity(match.Value, "GPE", match.Index, match.Index + match.Length, 0.6f));
        }

        // Years/dates
        foreach (Match match in YearRegex().Matches(text))
        {
            if (!IsOverlapping(entities, match.Index, match.Index + match.Length))
            {
                var year = int.Parse(match.Value);
                if (year >= 1900 && year <= 2100)
                    entities.Add(new ExtractedEntity(match.Value, "DATE", match.Index, match.Index + match.Length, 0.9f));
            }
        }

        return Task.FromResult(entities.OrderBy(e => e.Start).ToList());
    }

    public Task<List<ExtractedRelationship>> ExtractRelationshipsAsync(string text, CancellationToken cancellationToken = default)
    {
        var relationships = new List<ExtractedRelationship>();

        // =====================================================================
        // SOFTWARE RELATIONSHIPS
        // =====================================================================

        foreach (Match match in DependsOnRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "DEPENDS_ON", 0.85f));

        foreach (Match match in CallsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CALLS", 0.85f));

        foreach (Match match in DeployedToRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "DEPLOYED_TO", 0.85f));

        foreach (Match match in OwnsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value.Trim(), match.Groups[2].Value, "OWNS", 0.8f));

        foreach (Match match in ImplementsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "IMPLEMENTS", 0.85f));

        foreach (Match match in EmitsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "EMITS", 0.85f));

        foreach (Match match in ConsumesRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CONSUMES", 0.85f));

        foreach (Match match in FixesRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "FIXES", 0.9f));

        foreach (Match match in CausedRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CAUSED", 0.85f));

        foreach (Match match in TestsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "TESTS", 0.85f));

        foreach (Match match in WorksOnRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "WORKS_ON", 0.8f));

        // =====================================================================
        // HARDWARE/ENGINEERING RELATIONSHIPS
        // =====================================================================

        foreach (Match match in ConnectsToRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CONNECTS_TO", 0.9f));

        foreach (Match match in MountedOnRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "MOUNTED_ON", 0.9f));

        foreach (Match match in PowersRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "POWERS", 0.9f));

        foreach (Match match in ControlsRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "CONTROLS", 0.85f));

        foreach (Match match in MeasuresRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "MEASURES", 0.85f));

        foreach (Match match in CommunicatesViaRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value.ToUpperInvariant(), "COMMUNICATES_VIA", 0.9f));

        foreach (Match match in RatedForRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "RATED_FOR", 0.85f));

        foreach (Match match in CompliesWithRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value.ToUpperInvariant(), "COMPLIES_WITH", 0.9f));

        foreach (Match match in ManufacturedByRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, SoftwareGraphTypes.NormalizeEntityName(match.Groups[2].Value), "MANUFACTURED_BY", 0.9f));

        foreach (Match match in PartOfRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "PART_OF", 0.85f));

        foreach (Match match in InterfacesWithRegex().Matches(text))
            relationships.Add(new ExtractedRelationship(match.Groups[1].Value, match.Groups[2].Value, "INTERFACES_WITH", 0.85f));

        // Normalize relationship types - replace invalid types with RELATED_TO
        var normalizedRelationships = relationships.Select(rel =>
            SoftwareGraphTypes.IsValidRelationshipType(rel.RelationType)
                ? rel
                : new ExtractedRelationship(rel.SourceEntity, rel.TargetEntity, "RELATED_TO", rel.Confidence * 0.8f)
        ).ToList();

        return Task.FromResult(normalizedRelationships);
    }

    public async Task<(List<ExtractedEntity> Entities, List<ExtractedRelationship> Relationships)> ExtractAllAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var entities = await ExtractEntitiesAsync(text, cancellationToken);
        var relationships = await ExtractRelationshipsAsync(text, cancellationToken);
        return (entities, relationships);
    }

    private static bool IsOverlapping(List<ExtractedEntity> entities, int start, int end)
    {
        return entities.Any(e =>
            (start >= e.Start && start < e.End) ||
            (end > e.Start && end <= e.End) ||
            (start <= e.Start && end >= e.End));
    }
}
