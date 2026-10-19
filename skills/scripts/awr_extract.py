#!/usr/bin/env python3
"""AWR HTML 报告 -> 结构化表格文本（按章节分段，管道符分隔单元格）。

为什么不用「拍平成纯文本」：AWR 有 90% 的信息在表格里，拍平后表头与数据行的对应关系
会丢，导致引用数字时说不清来源。本脚本按文档顺序扫描 <h1..h4> 与 <table>，把每张表
挂到它前面最近的标题下，输出：

    ########## Top 10 Foreground Events by Total Wait Time
    Event | Waits | Time (s) | Avg wait | % DB time
    db file sequential read | 8,346,984 | 3,669.55 | 439.63us | 8.81

这样每个数字都能直接说「来自哪个章节的哪一列」，满足取证要求。

用法：
    python awr_extract.py report.html                      # 全量导出到 stdout
    python awr_extract.py report.html --dump out.txt       # 全量导出到文件（推荐）
    python awr_extract.py report.html --list               # 只列章节名（去重）
    python awr_extract.py report.html --section "Top 10"   # 抽匹配章节（含表）
    python awr_extract.py report.html --grep "WF_AUTOFAI"  # 搜索并打印所属章节
    python awr_extract.py report.html --json out.json      # 结构化输出，供程序消费
"""

import argparse
import html as html_mod
import json
import os
import re
import sys

TAG = re.compile(r"<[^>]+>")
TOKEN = re.compile(
    r"(?is)<h([1-4])[^>]*>(.*?)</h\1>"          # 1: 标题级别, 2: 标题文本
    r"|(<p\b[^>]*>)"                             # 3: <p> / <p /> 分隔符（19c 用 <p />标题<p />）
    r"|<table\b.*?</table>"                      # 4: 表格
)
ROW = re.compile(r"(?is)<tr\b.*?</tr>")
CELL = re.compile(r"(?is)<t[dh]\b.*?</t[dh]>")

# 19c 的 AWR 模板里 Load Profile / Instance Efficiency 等标题写成 <p />标题<p />，
# 不在 <hN> 里。只认白名单，避免把正文里的说明句误当章节名。
P_TITLE = re.compile(
    r"^(Load Profile|Instance Efficiency Percentages.*|Top 10 Foreground Events.*|"
    r"Wait Classes by Total Wait Time|Host CPU|Instance CPU|IO Profile|Memory Statistics|"
    r"Database Resource Limits|Cache Sizes|Shared Pool Statistics|Top 10 Channel Waits.*|"
    r"Time Model Statistics|Operating System Statistics.*|Foreground Wait Class.*|"
    r"Background Wait Class.*|SGA Target Advisory|PGA Aggr.*|Enqueue Activity|"
    r"Undo Segment.*|Latch .*|Segments by .*|Complete List of SQL Text)$",
    re.I,
)


def cell_text(s):
    s = TAG.sub(" ", s)
    s = html_mod.unescape(s)
    s = s.replace("\xa0", " ")
    return re.sub(r"\s+", " ", s).strip()


def parse_html_blocks(raw):
    """HTML 报告 -> [(section, [row_text, ...])]，行内单元格用 ' | ' 连接。"""
    blocks = []
    section = "(header)"
    for m in TOKEN.finditer(raw):
        if m.group(1):                      # <hN> 标题
            title = cell_text(m.group(2))
            if title:
                section = title[:120]
            continue
        if m.group(3) is not None:          # <p> 分隔符：其后紧跟的纯文本可能是章节标题
            tail = raw[m.end(): m.end() + 200]
            tail = tail.split("<", 1)[0]
            text = cell_text(tail)
            if text and P_TITLE.match(text):
                section = text[:120]
            continue
        body = m.group(0)                   # 表格
        rows = []
        for r in ROW.finditer(body):
            cells = [cell_text(c) for c in CELL.findall(r.group(0))]
            if any(cells):
                rows.append(" | ".join(cells))
        if rows:
            blocks.append((section, rows))
    return blocks


TXT_UNDERLINE = re.compile(r"^~{3,}")


def parse_txt_blocks(lines):
    """TXT 报告 -> [(section, [line, ...])]。

    AWR 的 txt 用「标题行 + 一排 ~ 下划线」界定章节，比 HTML 的标题更可靠。
    注意两种变体：
      - 标题独占一行：Top 10 Foreground Events by Total Wait Time
      - 标题行后面还接了列头：Load Profile            Per Second ...
    下划线行也可能带列分隔符：~~~~~~~~~~~  ------------
    直接保留原始列对齐，数值列在同一行内，SQL 文本的换行也保留原样。
    """
    marks = []
    for i in range(len(lines) - 1):
        if not lines[i].strip() or lines[i] != lines[i].strip():
            continue
        if TXT_UNDERLINE.match(lines[i]):          # 纯下划线行，不是标题
            continue
        # 两种标题形态：① 后跟 ~~~ 下划线；② SQL ordered by 系列（后面直接是 -> 说明与表头）
        if TXT_UNDERLINE.match(lines[i + 1]) or lines[i].startswith("SQL ordered by"):
            marks.append(i)

    blocks = []
    for k, start in enumerate(marks):
        end = marks[k + 1] if k + 1 < len(marks) else len(lines)
        line = lines[start].strip()
        # 标题行后面可能直接接列头（3+ 空格）或直接接 "DB/Inst:"（19c 全局报告会粘连）
        m = re.search(r"\s{3,}|DB/Inst:", line)
        if m:
            raw_title, rest = line[: m.start()].strip(), line[m.start():].strip()
        else:
            raw_title, rest = line, ""
        if not raw_title or raw_title.startswith("->"):
            continue
        title = raw_title[:120]
        body = [ln.rstrip() for ln in lines[start + 1:end]]
        body = [ln for ln in body if not TXT_UNDERLINE.match(ln)]
        if rest:                                     # 标题行后半截通常是列头，保留
            body.insert(0, rest)
        while body and not body[0].strip():
            body.pop(0)
        while body and not body[-1].strip():
            body.pop()
        if body:
            blocks.append((title, body))
    return blocks


def parse_blocks(path):
    """自动识别 HTML / TXT 两种 AWR 报告格式。"""
    with open(path, encoding="utf-8", errors="replace") as fh:
        raw = fh.read()
    raw = raw.replace("\r\n", "\n")
    head = raw[:2000].lower()
    if "<html" in head or "<table" in head or "<h3" in head:
        return parse_html_blocks(re.sub(r"(?is)<(script|style)[^>]*>.*?</\1>", " ", raw))
    return parse_txt_blocks(raw.splitlines())


def emit(blocks, out=sys.stdout):
    for section, rows in blocks:
        out.write(f"########## {section}\n")
        for r in rows:
            out.write(r + "\n")
        out.write("\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("html")
    ap.add_argument("--dump", help="全量导出到文件")
    ap.add_argument("--list", action="store_true", help="只列章节名")
    ap.add_argument("--section", help="按章节名子串抽取（不区分大小写）")
    ap.add_argument("--grep", help="按正则搜索，打印命中的整张表")
    ap.add_argument("--json", help="结构化输出到 JSON 文件")
    ap.add_argument("--max-rows", type=int, default=0, help="每张表最多输出 N 行（0=不限）")
    args = ap.parse_args()

    if not os.path.isfile(args.html):
        sys.exit(f"file not found: {args.html}")

    blocks = parse_blocks(args.html)
    if not blocks:
        sys.exit("[empty] 没有解析到任何表格，确认文件是 AWR 的 HTML 报告")

    if args.max_rows:
        blocks = [(s, r[: args.max_rows]) for s, r in blocks]

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump([{"section": s, "rows": r} for s, r in blocks], fh,
                      ensure_ascii=False, indent=1)
        print(f"[json] {args.json}  ({len(blocks)} tables)")

    if args.list:
        seen = []
        for s, _ in blocks:
            if s not in seen:
                seen.append(s)
        for s in seen:
            print(s)
        return

    if args.section:
        key = args.section.lower()
        hit = [(s, r) for s, r in blocks if key in s.lower()]
        if not hit:
            sys.exit(f"[not found] '{args.section}'，用 --list 查看章节名")
        emit(hit)
        return

    if args.grep:
        rx = re.compile(args.grep, re.I)
        hit = []
        for s, rows in blocks:
            if rx.search(s) or any(rx.search(" | ".join(r)) for r in rows):
                hit.append((s, rows))
        if not hit:
            sys.exit(f"[no match] {args.grep}")
        emit(hit)
        return

    if args.dump:
        with open(args.dump, "w", encoding="utf-8") as fh:
            emit(blocks, fh)
        print(f"[dump] {args.dump}  ({len(blocks)} tables)")
        return

    emit(blocks)


if __name__ == "__main__":
    main()
