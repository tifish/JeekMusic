import com.ibm.icu.text.Transliterator;
import com.ibm.icu.text.UnicodeSet;
import com.ibm.icu.text.UnicodeSetIterator;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * 从 ICU4J（和 Android 同源的 ICU 数据）生成桌面端用的拼音、繁简对照表（desktop/docs/design.md §6.4）。
 *
 * 移动端用 Android ICU 的 Transliterator："Han-Latin; Latin-ASCII"（排序键、拼音首字母）、
 * "Traditional-Simplified" / "Simplified-Traditional"（在线结果的比较）。.NET 没有转写功能，
 * 所以把这些转写拆成几步（见 desktop/src/JeekMusic.Core/Text/Pinyin.cs 的说明），每一步需要的表和字符集合都从 ICU 导出。
 * 生成时核对规则的结构，ICU 的规则变了（升级版本时）会报错，而不是悄悄生成不对的表。
 *
 * 用法：java -cp icu4j-76.1.jar Generate.java tables <输出目录>
 *       java -cp icu4j-76.1.jar Generate.java expect <语料.txt> <输出.tsv>   （每行一条语料，输出 ICU 的转写结果，供两端对比）
 */
public class Generate {

    public static void main(String[] args) throws IOException {
        switch (args[0]) {
            case "tables" -> tables(Path.of(args[1]));
            case "expect" -> expect(Path.of(args[1]), Path.of(args[2]));
            default -> throw new IllegalArgumentException(args[0]);
        }
    }

    /** 一条规则：raw 是 toRules(true) 的原文（非 ASCII 写成 \\uXXXX，可以原样交给 createFromRules），plain 是反转义后的，用来分析结构。 */
    record Rule(String raw, String plain) {
    }

    // ---------------- 生成表 ----------------

    static void tables(Path out) throws IOException {
        Files.createDirectories(out);
        List<Rule> spacedhan = rules("Han-Spacedhan");
        List<Rule> hanLatin = rules("Han-Latin");
        List<Rule> latinAscii = rules("Latin-ASCII");

        // 1. Han-Spacedhan 的前两轮：限定字符集的 NFKC + 全角转半角，逐字
        if (!spacedhan.get(0).plain.startsWith("::[") || !spacedhan.get(0).plain.endsWith("] nfkc;")
                || !spacedhan.get(1).plain.equals("::fullwidth-halfwidth;")) {
            throw new IllegalStateException("Han-Spacedhan 的开头变了");
        }
        String pre = spacedhan.get(0).raw + "\n" + spacedhan.get(1).raw + "\n";
        writeCharTable(out.resolve("spacedhan-pre.tsv"), Transliterator.createFromRules("pre", pre, Transliterator.FORWARD),
                "Han-Spacedhan 的前两轮（" + spacedhan.get(0).plain + " " + spacedhan.get(1).plain + "）逐字的结果");

        // 2. Han-Spacedhan 第三轮里的标点映射（单字 → 字符串）；同一轮还有两条插空格的规则，由代码实现
        StringBuilder punct = header("Han-Spacedhan 第三轮的标点映射：字\t结果");
        Pattern single = Pattern.compile("^(\\S) > (.*);$");
        List<String> spacing = new ArrayList<>();
        for (Rule r : spacedhan.subList(2, spacedhan.size())) {
            Matcher m = single.matcher(r.plain);
            if (m.matches()) {
                punct.append(escape(m.group(1))).append('\t').append(escape(unquote(m.group(2)))).append('\n');
            } else if (r.plain.contains("{}")) {
                spacing.add(r.plain);
            } else {
                throw new IllegalStateException("未识别的规则：" + r.plain);
            }
        }
        List<String> expectedSpacing = List.of(
                "[[:Ideographic:][.,\\:;?!．，：？！｡、；[:Pe:][:Pf:]]]{}[:Letter:] > ' ';",
                "[:Letter:][:Mark:]*{}[[:Ideographic:][:Ps:][:Pi:]] > ' ';");
        if (!spacing.equals(expectedSpacing)) throw new IllegalStateException("插空格的规则变了：" + spacing);
        Files.writeString(out.resolve("spacedhan-punct.tsv"), punct, StandardCharsets.UTF_8);

        // 3. Han-Latin：上下文规则（多音字）+ 逐字映射
        if (!hanLatin.get(0).plain.equals("::Han-Spacedhan();")) throw new IllegalStateException(hanLatin.get(0).plain);
        StringBuilder context = header("Han-Latin 的上下文规则：字\t后面的字（中间可以有一个空格）\t结果；优先于逐字映射");
        Pattern contextRule = Pattern.compile("^\\{(\\S)\\}' '\\?(\\S) > (\\S+);$");
        StringBuilder mappingRules = new StringBuilder();
        for (Rule r : hanLatin.subList(1, hanLatin.size())) {
            Matcher m = contextRule.matcher(r.plain);
            if (m.matches()) {
                context.append(m.group(1)).append('\t').append(m.group(2)).append('\t').append(m.group(3)).append('\n');
            } else if (isContextRule(r.plain) || r.plain.startsWith("::")) {
                throw new IllegalStateException("未识别的规则：" + r.plain);
            } else {
                mappingRules.append(r.raw).append('\n');
            }
        }
        Files.writeString(out.resolve("han-latin-context.tsv"), context, StandardCharsets.UTF_8);
        writeCharTable(out.resolve("han-latin.tsv"), Transliterator.createFromRules("han", mappingRules.toString(), Transliterator.FORWARD),
                "Han-Latin 的逐字映射（带声调，之后再经过 Latin-ASCII）");

        // 4. Latin-ASCII：全局过滤集合 + NFD + 去掉拉丁字母和数字后的附加符号 + NFC + 逐字映射
        String[] expectedHeader = {"::[[:Latin:][:Common:][:Inherited:][〇]];", "::NFD();", "[[:Latin:][0-9]]{[:Mn:]+} > ;", "::NFC();"};
        for (int i = 0; i < expectedHeader.length; i++) {
            if (!latinAscii.get(i).plain.equals(expectedHeader[i])) throw new IllegalStateException("Latin-ASCII 的开头变了：" + latinAscii.get(i).plain);
        }
        StringBuilder laRules = new StringBuilder();
        for (Rule r : latinAscii.subList(4, latinAscii.size())) {
            if (isContextRule(r.plain) || r.plain.startsWith("::")) throw new IllegalStateException("未识别的规则：" + r.plain);
            laRules.append(r.raw).append('\n');
        }
        writeCharTable(out.resolve("latin-ascii.tsv"), Transliterator.createFromRules("la", laRules.toString(), Transliterator.FORWARD),
                "Latin-ASCII 的逐字映射（NFC 之后）");

        // 5. 字符集合（不依赖 .NET 自己的 Unicode 数据版本）
        StringBuilder sets = header("字符集合：名字\t范围（十六进制 start-end，空格分隔）");
        set(sets, "SpacingPrev", "[[:Ideographic:][.,\\:;?!．，：？！｡、；[:Pe:][:Pf:]]]");
        set(sets, "Letter", "[:Letter:]");
        set(sets, "Mark", "[:Mark:]");
        set(sets, "SpacingNext", "[[:Ideographic:][:Ps:][:Pi:]]");
        set(sets, "LatinAsciiFilter", "[[:Latin:][:Common:][:Inherited:][〇]]");
        set(sets, "LatinOrDigit", "[[:Latin:][0-9]]");
        set(sets, "Mn", "[:Mn:]");
        // 移动端用 Character.UnicodeScript.HAN 判断"是不是汉字"（CharsetFixer、CoverMatcher）；.NET 没有文字属性
        set(sets, "Han", "[:sc=Han:]");
        Files.writeString(out.resolve("sets.tsv"), sets, StandardCharsets.UTF_8);

        // 6. 繁简：词组 + 单字（ICU 规则不允许前面的规则遮住后面的，所以等价于最长匹配）
        phraseTable(out.resolve("traditional-simplified.tsv"), "Traditional-Simplified");
        phraseTable(out.resolve("simplified-traditional.tsv"), "Simplified-Traditional");
    }

    /**
     * 繁简规则表，保持规则顺序：原文\t结果\t前文\t后文。前文、后文为空表示没有；"[...]" 是字符集合（逐字列出），否则是字符串。
     * 匹配方式同 ICU：在每个位置按顺序尝试以这个字开头的规则，第一条（原文、前文、后文都对得上的）生效。
     * 和原文一样的规则（如 "姜 > 姜"）也要保留：它们挡住了后面更短或带上下文的规则。
     */
    static void phraseTable(Path file, String id) throws IOException {
        StringBuilder sb = header(id + "：原文\t结果\t前文\t后文（按规则顺序；同一个位置第一条对得上的生效）");
        Pattern contextRule = Pattern.compile("^(\\[[^\\]]*\\])?\\{(\\S+?)\\}(\\[[^\\]]*\\]|\\S*) > (\\S*);$");
        Pattern rule = Pattern.compile("^(\\S+) > (\\S*);$");
        int count = 0;
        for (Rule r : rules(id)) {
            if (r.plain.startsWith("::") || r.plain.contains("'") || r.plain.contains("\\")) throw new IllegalStateException("未识别的规则：" + r.plain);
            Matcher c = contextRule.matcher(r.plain);
            if (c.matches()) {
                sb.append(c.group(2)).append('\t').append(c.group(4)).append('\t')
                        .append(expandSet(c.group(1))).append('\t').append(expandSet(c.group(3))).append('\n');
                count++;
                continue;
            }
            Matcher m = rule.matcher(r.plain);
            if (!m.matches()) throw new IllegalStateException("未识别的规则：" + r.plain);
            String key = m.group(1);
            String value = m.group(2);
            if (key.startsWith("[")) {
                for (UnicodeSetIterator it = new UnicodeSetIterator(new UnicodeSet(key)); it.next(); ) {
                    sb.append(it.getString()).append('\t').append(value).append("\t\t\n");
                    count++;
                }
            } else {
                sb.append(key).append('\t').append(value).append("\t\t\n");
                count++;
            }
        }
        Files.writeString(file, sb, StandardCharsets.UTF_8);
        System.out.println(file.getFileName() + ": " + count);
    }

    /** "[一-三]" → "[一二三]"（逐字列出）；不是集合的原样返回，null 返回空串。 */
    static String expandSet(String s) {
        if (s == null || s.isEmpty()) return "";
        if (!s.startsWith("[")) return s;
        StringBuilder sb = new StringBuilder("[");
        for (UnicodeSetIterator it = new UnicodeSetIterator(new UnicodeSet(s)); it.next(); ) sb.append(it.getString());
        return sb.append(']').toString();
    }

    /** 对每个码位（跳过代理项）单独转写，只写出结果和原字符不同的。 */
    static void writeCharTable(Path file, Transliterator t, String title) throws IOException {
        StringBuilder sb = header(title);
        int count = 0;
        for (int cp = 0; cp <= 0x10FFFF; cp++) {
            if (cp >= 0xD800 && cp <= 0xDFFF) continue;
            String s = new String(Character.toChars(cp));
            String r = t.transliterate(s);
            if (!r.equals(s)) {
                sb.append(escape(s)).append('\t').append(escape(r)).append('\n');
                count++;
            }
        }
        Files.writeString(file, sb, StandardCharsets.UTF_8);
        System.out.println(file.getFileName() + ": " + count);
    }

    static void set(StringBuilder sb, String name, String pattern) {
        UnicodeSet set = new UnicodeSet(pattern);
        sb.append(name).append('\t');
        for (int i = 0; i < set.getRangeCount(); i++) {
            if (i > 0) sb.append(' ');
            sb.append(Integer.toHexString(set.getRangeStart(i)).toUpperCase()).append('-').append(Integer.toHexString(set.getRangeEnd(i)).toUpperCase());
        }
        sb.append('\n');
    }

    static StringBuilder header(String title) {
        return new StringBuilder("# 由 shared/pinyin/Generate.java 用 ICU4J ").append(com.ibm.icu.util.VersionInfo.ICU_VERSION)
                .append(" 生成，不要手改。").append(title).append('\n');
    }

    static List<Rule> rules(String id) {
        List<Rule> result = new ArrayList<>();
        for (String line : Transliterator.getInstance(id).toRules(true).split("\n")) {
            String t = line.strip();
            if (!t.isEmpty()) result.add(new Rule(t, plain(t)));
        }
        return result;
    }

    /** 反转义 toRules(true) 里的 \\uXXXX、\\UXXXXXXXX。 */
    static String plain(String s) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\\' && i + 5 < s.length() && s.charAt(i + 1) == 'u') {
                sb.append((char) Integer.parseInt(s.substring(i + 2, i + 6), 16));
                i += 5;
            } else if (c == '\\' && i + 9 < s.length() && s.charAt(i + 1) == 'U') {
                sb.appendCodePoint(Integer.parseInt(s.substring(i + 2, i + 10), 16));
                i += 9;
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }

    /** 规则左边（" > " 之前）有花括号就是带上下文的规则；右边引号里的 '{' 只是普通字符。 */
    static boolean isContextRule(String line) {
        int arrow = line.indexOf(" > ");
        String left = arrow < 0 ? line : line.substring(0, arrow);
        return left.contains("{") || left.contains("}");
    }

    /** 规则右边的引号：'(A)' → (A)，'' → '，\' → '。 */
    static String unquote(String s) {
        StringBuilder sb = new StringBuilder();
        boolean quoted = false;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\\' && i + 1 < s.length()) {
                sb.append(s.charAt(++i));
            } else if (c == '\'') {
                if (i + 1 < s.length() && s.charAt(i + 1) == '\'') {
                    sb.append('\'');
                    i++;
                } else {
                    quoted = !quoted;
                }
            } else if (!quoted && c == ' ') {
                // 引号外的空格在 ICU 规则里没有意义
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }

    /** 表里的反斜杠、制表符、换行写成 \\、\t、\n。 */
    static String escape(String s) {
        return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n");
    }

    static String unescape(String s) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\\' && i + 1 < s.length()) {
                char n = s.charAt(++i);
                sb.append(n == 't' ? '\t' : n == 'n' ? '\n' : n);
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }

    // ---------------- 生成期望值 ----------------

    static void expect(Path corpus, Path out) throws IOException {
        Transliterator pinyin = Transliterator.getInstance("Han-Latin; Latin-ASCII");
        Transliterator t2s = Transliterator.getInstance("Traditional-Simplified");
        Transliterator s2t = Transliterator.getInstance("Simplified-Traditional");
        StringBuilder sb = new StringBuilder();
        for (String line : Files.readAllLines(corpus, StandardCharsets.UTF_8)) {
            if (line.isEmpty()) continue;
            String input = unescape(line);
            sb.append(escape(input)).append('\t')
                    .append(escape(pinyin.transliterate(input))).append('\t')
                    .append(escape(t2s.transliterate(input))).append('\t')
                    .append(escape(s2t.transliterate(input))).append('\n');
        }
        Files.writeString(out, sb, StandardCharsets.UTF_8);
    }
}
