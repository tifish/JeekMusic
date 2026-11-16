package com.tinyfish.jeekmusic.data.sync

import org.json.JSONTokener

/** Strict JSON ingress before org.json's deliberately permissive legacy parser. */
internal object GroupJson {
    fun parse(text: String): Any? {
        var position=0;var tokens=0
        fun invalid(): Nothing=throw GroupSyncException("invalid_user_data")
        fun whitespace() { while(position<text.length && text[position] in " \r\n\t")position++ }
        fun string(): String {
            if(position>=text.length || text[position++]!='"')invalid()
            val result=StringBuilder()
            while(true) {
                if(position>=text.length)invalid()
                val char=text[position++]
                if(char=='"')break
                if(char<' ')invalid()
                if(char!='\\')result.append(char)
                else {
                    if(position>=text.length)invalid()
                    result.append(when(val escaped=text[position++]) {
                        '"','\\','/'->escaped;'b'->'\b';'f'->'\u000c';'n'->'\n';'r'->'\r';'t'->'\t'
                        'u'->{ if(position+4>text.length)invalid();val code=text.substring(position,position+4).toIntOrNull(16) ?: invalid();position+=4;code.toChar() }
                        else->invalid()
                    })
                }
            }
            var index=0
            while(index<result.length) {
                val char=result[index++]
                if(char.isHighSurrogate()) { if(index>=result.length || !result[index++].isLowSurrogate())invalid() }
                else if(char.isLowSurrogate())invalid()
            }
            return result.toString()
        }
        fun value(depth: Int) {
            if(depth>32 || ++tokens>1_000_000)invalid()
            whitespace();if(position>=text.length)invalid()
            when(text[position]) {
                '"'->string()
                '{'->{
                    position++;whitespace();val keys=mutableSetOf<String>()
                    if(position<text.length && text[position]=='}')position++
                    else while(true) {
                        whitespace();if(!keys.add(string()))invalid();whitespace()
                        if(position>=text.length || text[position++]!=':')invalid()
                        value(depth+1);whitespace();if(position>=text.length)invalid()
                        val next=text[position++];if(next=='}')break;if(next!=',')invalid()
                    }
                }
                '['->{
                    position++;whitespace()
                    if(position<text.length && text[position]==']')position++
                    else while(true) { value(depth+1);whitespace();if(position>=text.length)invalid();val next=text[position++];if(next==']')break;if(next!=',')invalid() }
                }
                't','f','n'->{
                    val literal=when(text[position]) { 't'->"true";'f'->"false";else->"null" }
                    if(!text.startsWith(literal,position))invalid();position+=literal.length
                }
                else->{
                    val start=position
                    while(position<text.length && text[position] in "-+0123456789.eE")position++
                    if(!text.substring(start,position).matches(Regex("-?(0|[1-9][0-9]*)(\\.[0-9]+)?([eE][+-]?[0-9]+)?")))invalid()
                }
            }
        }
        value(0);whitespace();if(position!=text.length)invalid()
        return JSONTokener(text).nextValue()
    }
}
