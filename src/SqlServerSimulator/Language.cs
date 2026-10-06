namespace SqlServerSimulator;

/// <summary>
/// One row of SQL Server's installed-language table — the set <c>SET LANGUAGE</c>
/// resolves against, <c>@@LANGUAGE</c> / <c>@@LANGID</c> report, and
/// <c>sys.syslanguages</c> projects.
/// </summary>
/// <remarks>
/// The 34 rows are a stock SQL Server 2025 instance's, captured verbatim
/// (2026-08-08; the month and weekday name lists 2026-09-30). <see cref="DateFirst"/> and <see cref="DateFormat"/> are the
/// load-bearing columns: a successful <c>SET LANGUAGE</c> carries them into
/// the session's <c>@@DATEFIRST</c> and <c>SET DATEFORMAT</c> order.
/// </remarks>
internal sealed class Language(short langId, string name, string alias, string dateFormat, byte dateFirst, int lcid, short msgLangId, string months, string shortMonths, string days)
{
    public readonly short LangId = langId;
    public readonly string Name = name;
    public readonly string Alias = alias;
    public readonly string DateFormat = dateFormat;
    public readonly byte DateFirst = dateFirst;
    public readonly int Lcid = lcid;
    public readonly short MsgLangId = msgLangId;

    /// <summary>The comma-separated month, abbreviated month and weekday names <c>sys.syslanguages</c> and <c>sp_helplanguage</c> report (weekdays start on Monday).</summary>
    public readonly string Months = months;

    /// <inheritdoc cref="Months"/>
    public readonly string ShortMonths = shortMonths;

    /// <inheritdoc cref="Months"/>
    public readonly string Days = days;

    /// <summary><see cref="Months"/> one per element, January first: the names <c>DATENAME(month, …)</c> answers.</summary>
    public readonly string[] MonthNames = months.Split(',');

    /// <summary><see cref="ShortMonths"/> one per element, January first.</summary>
    public readonly string[] ShortMonthNames = shortMonths.Split(',');

    /// <summary>
    /// <see cref="Days"/> one per element, reordered Sunday first: the names
    /// <c>DATENAME(weekday, …)</c> answers.
    /// </summary>
    public readonly string[] DayNames = [days.Split(',')[6], .. days.Split(',')[..6]];

    /// <summary>
    /// Every installed language, in <c>langid</c> order — which is also
    /// <c>sys.syslanguages</c>'s own order.
    /// </summary>
    public static readonly Language[] All =
    [
        new(0, "us_english", "English", "mdy", 7, 1033, 1033, "January,February,March,April,May,June,July,August,September,October,November,December", "Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec", "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday"),
        new(1, "Deutsch", "German", "dmy", 1, 1031, 1031, "Januar,Februar,März,April,Mai,Juni,Juli,August,September,Oktober,November,Dezember", "Jan,Feb,Mär,Apr,Mai,Jun,Jul,Aug,Sep,Okt,Nov,Dez", "Montag,Dienstag,Mittwoch,Donnerstag,Freitag,Samstag,Sonntag"),
        new(2, "Français", "French", "dmy", 1, 1036, 1036, "janvier,février,mars,avril,mai,juin,juillet,août,septembre,octobre,novembre,décembre", "janv,févr,mars,avr,mai,juin,juil,août,sept,oct,nov,déc", "lundi,mardi,mercredi,jeudi,vendredi,samedi,dimanche"),
        new(3, "日本語", "Japanese", "ymd", 7, 1041, 1041, "01,02,03,04,05,06,07,08,09,10,11,12", "01,02,03,04,05,06,07,08,09,10,11,12", "月曜日,火曜日,水曜日,木曜日,金曜日,土曜日,日曜日"),
        new(4, "Dansk", "Danish", "dmy", 1, 1030, 1030, "januar,februar,marts,april,maj,juni,juli,august,september,oktober,november,december", "jan,feb,mar,apr,maj,jun,jul,aug,sep,okt,nov,dec", "mandag,tirsdag,onsdag,torsdag,fredag,lørdag,søndag"),
        new(5, "Español", "Spanish", "dmy", 1, 3082, 3082, "Enero,Febrero,Marzo,Abril,Mayo,Junio,Julio,Agosto,Septiembre,Octubre,Noviembre,Diciembre", "Ene,Feb,Mar,Abr,May,Jun,Jul,Ago,Sep,Oct,Nov,Dic", "Lunes,Martes,Miércoles,Jueves,Viernes,Sábado,Domingo"),
        new(6, "Italiano", "Italian", "dmy", 1, 1040, 1040, "gennaio,febbraio,marzo,aprile,maggio,giugno,luglio,agosto,settembre,ottobre,novembre,dicembre", "gen,feb,mar,apr,mag,giu,lug,ago,set,ott,nov,dic", "lunedì,martedì,mercoledì,giovedì,venerdì,sabato,domenica"),
        new(7, "Nederlands", "Dutch", "dmy", 1, 1043, 1043, "januari,februari,maart,april,mei,juni,juli,augustus,september,oktober,november,december", "jan,feb,mrt,apr,mei,jun,jul,aug,sep,okt,nov,dec", "maandag,dinsdag,woensdag,donderdag,vrijdag,zaterdag,zondag"),
        new(8, "Norsk", "Norwegian", "dmy", 1, 2068, 2068, "januar,februar,mars,april,mai,juni,juli,august,september,oktober,november,desember", "jan,feb,mar,apr,mai,jun,jul,aug,sep,okt,nov,des", "mandag,tirsdag,onsdag,torsdag,fredag,lørdag,søndag"),
        new(9, "Português", "Portuguese", "dmy", 7, 2070, 2070, "janeiro,fevereiro,março,abril,maio,junho,julho,agosto,setembro,outubro,novembro,dezembro", "jan,fev,mar,abr,mai,jun,jul,ago,set,out,nov,dez", "segunda-feira,terça-feira,quarta-feira,quinta-feira,sexta-feira,sábado,domingo"),
        new(10, "Suomi", "Finnish", "dmy", 1, 1035, 1035, "tammikuuta,helmikuuta,maaliskuuta,huhtikuuta,toukokuuta,kesäkuuta,heinäkuuta,elokuuta,syyskuuta,lokakuuta,marraskuuta,joulukuuta", "tammi,helmi,maalis,huhti,touko,kesä,heinä,elo,syys,loka,marras,joulu", "maanantai,tiistai,keskiviikko,torstai,perjantai,lauantai,sunnuntai"),
        new(11, "Svenska", "Swedish", "ymd", 1, 1053, 1053, "januari,februari,mars,april,maj,juni,juli,augusti,september,oktober,november,december", "jan,feb,mar,apr,maj,jun,jul,aug,sep,okt,nov,dec", "måndag,tisdag,onsdag,torsdag,fredag,lördag,söndag"),
        new(12, "čeština", "Czech", "dmy", 1, 1029, 1029, "leden,únor,březen,duben,květen,červen,červenec,srpen,září,říjen,listopad,prosinec", "I,II,III,IV,V,VI,VII,VIII,IX,X,XI,XII", "pondělí,úterý,středa,čtvrtek,pátek,sobota,neděle"),
        new(13, "magyar", "Hungarian", "ymd", 1, 1038, 1038, "január,február,március,április,május,június,július,augusztus,szeptember,október,november,december", "jan,febr,márc,ápr,máj,jún,júl,aug,szept,okt,nov,dec", "hétfő,kedd,szerda,csütörtök,péntek,szombat,vasárnap"),
        new(14, "polski", "Polish", "dmy", 1, 1045, 1045, "styczeń,luty,marzec,kwiecień,maj,czerwiec,lipiec,sierpień,wrzesień,październik,listopad,grudzień", "I,II,III,IV,V,VI,VII,VIII,IX,X,XI,XII", "poniedziałek,wtorek,środa,czwartek,piątek,sobota,niedziela"),
        new(15, "română", "Romanian", "dmy", 1, 1048, 1048, "ianuarie,februarie,martie,aprilie,mai,iunie,iulie,august,septembrie,octombrie,noiembrie,decembrie", "Ian,Feb,Mar,Apr,Mai,Iun,Iul,Aug,Sep,Oct,Nov,Dec", "luni,marţi,miercuri,joi,vineri,sîmbătă,duminică"),
        new(16, "hrvatski", "Croatian", "ymd", 1, 1050, 1050, "siječanj,veljača,ožujak,travanj,svibanj,lipanj,srpanj,kolovoz,rujan,listopad,studeni,prosinac", "sij,vel,ožu,tra,svi,lip,srp,kol,ruj,lis,stu,pro", "ponedjeljak,utorak,srijeda,četvrtak,petak,subota,nedjelja"),
        new(17, "slovenčina", "Slovak", "dmy", 1, 1051, 1051, "január,február,marec,apríl,máj,jún,júl,august,september,október,november,december", "I,II,III,IV,V,VI,VII,VIII,IX,X,XI,XII", "pondelok,utorok,streda,štvrtok,piatok,sobota,nedeľa"),
        new(18, "slovenski", "Slovenian", "dmy", 1, 1060, 1060, "januar,februar,marec,april,maj,junij,julij,avgust,september,oktober,november,december", "jan,feb,mar,apr,maj,jun,jul,avg,sept,okt,nov,dec", "ponedeljek,torek,sreda,četrtek,petek,sobota,nedelja"),
        new(19, "ελληνικά", "Greek", "dmy", 1, 1032, 1032, "Ιανουαρίου,Φεβρουαρίου,Μαρτίου,Απριλίου,Μα_ου,Ιουνίου,Ιουλίου,Αυγούστου,Σεπτεμβρίου,Οκτωβρίου,Νοεμβρίου,Δεκεμβρίου", "Ιαν,Φεβ,Μαρ,Απρ,Μαϊ,Ιουν,Ιουλ,Αυγ,Σεπ,Οκτ,Νοε,Δεκ", "Δευτέρα,Τρίτη,Τετάρτη,Πέμπτη,Παρασκευή,Σάββατο,Κυριακή"),
        new(20, "български", "Bulgarian", "dmy", 1, 1026, 1026, "януари,февруари,март,април,май,юни,юли,август,септември,октомври,ноември,декември", "януари,февруари,март,април,май,юни,юли,август,септември,октомври,ноември,декември", "понеделник,вторник,сряда,четвъртък,петък,събота,неделя"),
        new(21, "русский", "Russian", "dmy", 1, 1049, 1049, "Январь,Февраль,Март,Апрель,Май,Июнь,Июль,Август,Сентябрь,Октябрь,Ноябрь,Декабрь", "янв,фев,мар,апр,май,июн,июл,авг,сен,окт,ноя,дек", "понедельник,вторник,среда,четверг,пятница,суббота,воскресенье"),
        new(22, "Türkçe", "Turkish", "dmy", 1, 1055, 1055, "Ocak,Şubat,Mart,Nisan,Mayıs,Haziran,Temmuz,Ağustos,Eylül,Ekim,Kasım,Aralık", "Oca,Şub,Mar,Nis,May,Haz,Tem,Ağu,Eyl,Eki,Kas,Ara", "Pazartesi,Salı,Çarşamba,Perşembe,Cuma,Cumartesi,Pazar"),
        new(23, "British", "British English", "dmy", 1, 2057, 1033, "January,February,March,April,May,June,July,August,September,October,November,December", "Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec", "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday"),
        new(24, "eesti", "Estonian", "dmy", 1, 1061, 1061, "jaanuar,veebruar,märts,aprill,mai,juuni,juuli,august,september,oktoober,november,detsember", "jaan,veebr,märts,apr,mai,juuni,juuli,aug,sept,okt,nov,dets", "esmaspäev,teisipäev,kolmapäev,neljapäev,reede,laupäev,pühapäev"),
        new(25, "latviešu", "Latvian", "ymd", 1, 1062, 1062, "janvāris,februāris,marts,aprīlis,maijs,jūnijs,jūlijs,augusts,septembris,oktobris,novembris,decembris", "jan,feb,mar,apr,mai,jūn,jūl,aug,sep,okt,nov,dec", "pirmdiena,otrdiena,trešdiena,ceturtdiena,piektdiena,sestdiena,svētdiena"),
        new(26, "lietuvių", "Lithuanian", "ymd", 1, 1063, 1063, "sausis,vasaris,kovas,balandis,gegužė,birželis,liepa,rugpjūtis,rugsėjis,spalis,lapkritis,gruodis", "sau,vas,kov,bal,geg,bir,lie,rgp,rgs,spl,lap,grd", "pirmadienis,antradienis,trečiadienis,ketvirtadienis,penktadienis,šeštadienis,sekmadienis"),
        new(27, "Português (Brasil)", "Brazilian", "dmy", 7, 1046, 1046, "Janeiro,Fevereiro,Março,Abril,Maio,Junho,Julho,Agosto,Setembro,Outubro,Novembro,Dezembro", "Jan,Fev,Mar,Abr,Mai,Jun,Jul,Ago,Set,Out,Nov,Dez", "Segunda-Feira,Terça-Feira,Quarta-Feira,Quinta-Feira,Sexta-Feira,Sábado,Domingo"),
        new(28, "繁體中文", "Traditional Chinese", "ymd", 7, 1028, 1028, "一月,二月,三月,四月,五月,六月,七月,八月,九月,十月,十一月,十二月", "01,02,03,04,05,06,07,08,09,10,11,12", "星期一,星期二,星期三,星期四,星期五,星期六,星期日"),
        new(29, "한국어", "Korean", "ymd", 7, 1042, 1042, "01,02,03,04,05,06,07,08,09,10,11,12", "01,02,03,04,05,06,07,08,09,10,11,12", "월요일,화요일,수요일,목요일,금요일,토요일,일요일"),
        new(30, "简体中文", "Simplified Chinese", "ymd", 7, 2052, 2052, "01,02,03,04,05,06,07,08,09,10,11,12", "01,02,03,04,05,06,07,08,09,10,11,12", "星期一,星期二,星期三,星期四,星期五,星期六,星期日"),
        new(31, "Arabic", "Arabic", "dmy", 1, 1025, 1025, "Muharram,Safar,Rabie I,Rabie II,Jumada I,Jumada II,Rajab,Shaaban,Ramadan,Shawwal,Thou Alqadah,Thou Alhajja", "Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec", "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday"),
        new(32, "ไทย", "Thai", "dmy", 7, 1054, 1054, "มกราคม,กุมภาพันธ์,มีนาคม,เมษายน,พฤษภาคม,มิถุนายน,กรกฎาคม,สิงหาคม,กันยายน,ตุลาคม,พฤศจิกายน,ธันวาคม", "ม.ค.,ก.พ.,มี.ค.,เม.ย.,พ.ค.,มิ.ย.,ก.ค.,ส.ค.,ก.ย.,ต.ค.,พ.ย.,ธ.ค.", "จันทร์,อังคาร,พุธ,พฤหัสบดี,ศุกร์,เสาร์,อาทิตย์"),
        new(33, "norsk (bokmål)", "Bokmål", "dmy", 1, 1044, 1044, "januar,februar,mars,april,mai,juni,juli,august,september,oktober,november,desember", "jan,feb,mar,apr,mai,jun,jul,aug,sep,okt,nov,des", "mandag,tirsdag,onsdag,torsdag,fredag,lørdag,søndag"),
    ];

    /// <summary>
    /// The culture <c>FORMAT</c> uses for this language when the call names
    /// none (probed 2026-10-04 against SQL Server 2025: <c>'MMMM'</c> reads
    /// <c>février</c> under <c>SET LANGUAGE French</c>).
    /// </summary>
    public string CultureName => this.Lcid switch
    {
        1025 => "ar-SA",
        1026 => "bg-BG",
        1028 => "zh-TW",
        1029 => "cs-CZ",
        1030 => "da-DK",
        1031 => "de-DE",
        1032 => "el-GR",
        1035 => "fi-FI",
        1036 => "fr-FR",
        1038 => "hu-HU",
        1040 => "it-IT",
        1041 => "ja-JP",
        1042 => "ko-KR",
        1043 => "nl-NL",
        1044 => "nb-NO",
        1045 => "pl-PL",
        1046 => "pt-BR",
        1048 => "ro-RO",
        1049 => "ru-RU",
        1050 => "hr-HR",
        1051 => "sk-SK",
        1053 => "sv-SE",
        1054 => "th-TH",
        1055 => "tr-TR",
        1060 => "sl-SI",
        1061 => "et-EE",
        1062 => "lv-LV",
        1063 => "lt-LT",
        2052 => "zh-CN",
        2057 => "en-GB",
        2068 => "nn-NO",
        2070 => "pt-PT",
        3082 => "es-ES",
        _ => "en-US",
    };

    /// <summary>
    /// Msg 2528, the line every DBCC command closes with, in this language's
    /// words where SQL Server installs its messages and in English elsewhere
    /// (read 2026-10-06 from SQL Server 2025's <c>sys.messages</c>).
    /// </summary>
    public string DbccCompletedMessage => this.MsgLangId switch
    {
        1028 => "DBCC 的執行已經完成。如果 DBCC 印出錯誤訊息，請連絡您的系統管理員。",
        1029 => "Příkaz DBCC byl dokončen. Pokud příkaz DBCC vytiskl chybové zprávy, obraťte se na správce systému.",
        1030 => "DBCC-udførelsen blev afsluttet. Hvis DBCC udskrev fejlmeddelelser, skal du kontakte systemadministratoren.",
        1031 => "Die DBCC-Ausführung wurde abgeschlossen. Falls DBCC Fehlermeldungen ausgegeben hat, wenden Sie sich an den Systemadministrator.",
        1032 => "Η εκτέλεση DBCC ολοκληρώθηκε. Αν η DBCC εκτύπωσε μηνύματα σφάλματος, επικοινωνήστε με το διαχειριστή του συστήματος.",
        1035 => "DBCC-toiminto on suoritettu. Jos DBCC tulosti virhesanomia, ota yhteyttä järjestelmänvalvojaan.",
        1036 => "Exécution de DBCC terminée. Si DBCC vous a adressé des messages d'erreur, contactez l'administrateur système.",
        1038 => "A DBCC-utasítás végrehajtása befejeződött. Ha a DBCC hibaüzeneteket írt ki, forduljon a rendszergazdához.",
        1040 => "Esecuzione DBCC completata. Se sono stati visualizzati messaggi di errore DBCC, rivolgersi all'amministratore di sistema.",
        1041 => "DBCC の実行が完了しました。DBCC がエラー メッセージを出力した場合は、システム管理者に相談してください。",
        1042 => "DBCC 실행이 완료되었습니다. DBCC에서 오류 메시지를 출력하면 시스템 관리자에게 문의하십시오.",
        1043 => "De DBCC-uitvoering is voltooid. Neem contact op met de systeembeheerder als DBCC foutberichten heeft afgedrukt.",
        1044 => "DBCC-utførelse er fullført. Kontakt systemansvarlig hvis DBCC skrev ut feilmeldinger.",
        1045 => "Wykonywanie polecenia DBCC zostało ukończone. Jeśli program DBCC wygenerował komunikaty o błędach, skontaktuj się z administratorem systemu.",
        1046 => "A execução do DBCC foi concluída. Se o DBCC imprimiu mensagens de erro, entre em contato com o administrador do sistema.",
        1049 => "Выполнение DBCC завершено. Если DBCC выдает сообщения об ошибках, обратитесь к системному администратору.",
        1053 => "DBCC-körningen slutfördes. Om DBCC returnerade felmeddelanden kontaktar du systemadministratören.",
        1055 => "DBCC yürütme tamamlandı. DBCC hata iletileri çıkarırsa, sistem yöneticinize başvurun.",
        2052 => "DBCC 执行完毕。如果 DBCC 输出了错误信息，请与系统管理员联系。",
        2070 => "Execução de DBCC concluída. Se o DBCC imprimir mensagens de erro, contacte o administrador de sistema.",
        3082 => "Ejecución de DBCC completada. Si hay mensajes de error, consulte al administrador del sistema.",
        _ => "DBCC execution completed. If DBCC printed error messages, contact your system administrator.",
    };

    /// <summary>
    /// Msg 5703 as a <c>SET LANGUAGE</c> to this language sends it, in the
    /// language's own words where SQL Server installs its messages (captured
    /// 2026-10-04 from SQL Server 2025).
    /// </summary>
    public string ChangedMessage => this.LangId switch
    {
        1 => $"Die Spracheneinstellung wurde in {this.Name} geändert.",
        2 => $"Le paramètre de langue est passé à {this.Name}.",
        3 => $"言語設定が {this.Name} に変更されました。",
        4 => $"Sprogindstillingen blev ændret til {this.Name}.",
        5 => $"Se cambió la configuración de idioma a {this.Name}.",
        6 => $"L'impostazione della lingua è stata sostituita con {this.Name}.",
        7 => $"De taalinstelling is gewijzigd in {this.Name}.",
        9 => $"A definição de idioma foi alterada para {this.Name}.",
        10 => $"Kieliasetukseksi muutettiin {this.Name}.",
        11 => $"Ändrade språkinställningen till {this.Name}.",
        12 => $"Nastavení jazyka bylo změněno na {this.Name}.",
        13 => $"Nyelvi beállítás átállítva a következőre: {this.Name}.",
        14 => $"Zmieniono ustawienia języka na {this.Name}.",
        19 => $"Η ρύθμιση γλώσσας άλλαξε σε {this.Name}.",
        21 => $"Язык изменен на {this.Name}.",
        22 => $"Dil ayarı '{this.Name}' olarak değiştirildi.",
        27 => $"Definição do idioma alterada para {this.Name}.",
        28 => $"已將語言設定變更為 {this.Name}。",
        29 => $"언어 설정이 {this.Name}(으)로 변경되었습니다.",
        30 => $"已将语言设置更改为 {this.Name}。",
        33 => $"Endret språkinnstilling til {this.Name}.",
        _ => $"Changed language setting to {this.Name}.",
    };

    private static readonly AsyncLocal<Language?> current = new();

    /// <summary>
    /// The language of the session whose statement is running, published by
    /// the dispatch loop beside <see cref="Storage.DateOrder.Current"/> for the
    /// string → date-time conversion, whose month names follow it. Unset, it
    /// is <see cref="Default"/>.
    /// </summary>
    public static Language Current
    {
        get => current.Value ?? Default;
        set => current.Value = value;
    }

    /// <summary>The instance default — <c>us_english</c>, langid 0, DATEFIRST 7.</summary>
    public static readonly Language Default = All[0];

    /// <summary>
    /// Resolves an official name or an alias, the way <c>SET LANGUAGE</c> does.
    /// The match is case-insensitive; a name carrying diacritics
    /// (<c>Français</c>, <c>čeština</c>) has to be spelled with them, since
    /// real compares against the stored name rather than a folded form.
    /// </summary>
    public static Language? Find(string nameOrAlias)
    {
        foreach (var language in All)
        {
            if (string.Equals(language.Name, nameOrAlias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(language.Alias, nameOrAlias, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }
        return null;
    }
}
