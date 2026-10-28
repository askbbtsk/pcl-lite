import re, os, sys, glob

ROOT = r'E:\DESKTOP\project\Pcl-lite\PCL-CE\Plain Craft Launcher 2'

# 控件 -> Click 事件的第二个参数类型（依据各控件自己的 ClickEventHandler 定义）
CLICK_PARAM = {
    'MyButton': 'MouseButtonEventArgs',
    'MyIconTextButton': 'ModBase.RouteEventArgs',
    'MyIconButton': 'EventArgs',
    'MyExtraButton': 'MouseButtonEventArgs',
    'MyExtraTextButton': 'MouseButtonEventArgs',
    'MyTextButton': 'EventArgs',
    'MyListItem': 'MouseButtonEventArgs',
    'MyCard': None,
    'Button': 'RoutedEventArgs',
}

ELEM_RE = re.compile(r'<(?:\w+:)?([A-Za-z_]\w*)((?:\s+[^<>]*?)?)/?>', re.S)
ATTR_RE = re.compile(r'([A-Za-z_][\w.:]*)="([^"]*)"')

problems = []
checked = 0

for xaml in glob.glob(os.path.join(ROOT, '**', '*.xaml'), recursive=True):
    if '\\obj\\' in xaml or '\\bin\\' in xaml:
        continue
    cs = xaml + '.cs'
    if not os.path.exists(cs):
        continue
    src = open(xaml, encoding='utf-8-sig').read()
    code = open(cs, encoding='utf-8-sig').read()

    for m in ELEM_RE.finditer(src):
        tag, attrtext = m.group(1), m.group(2)
        attrs = dict(ATTR_RE.findall(attrtext))
        handler = attrs.get('Click')
        if not handler:
            continue
        expected = CLICK_PARAM.get(tag, '<未知控件>')
        # 在 code-behind 里找这个方法
        hm = re.search(r'void\s+' + re.escape(handler) + r'\s*\(([^)]*)\)', code)
        if not hm:
            problems.append((xaml, handler, tag, expected, '方法不存在'))
            continue
        # 去掉 "= 默认值" 再取类型
        params = [p.split('=')[0].strip() for p in hm.group(1).split(',') if p.split('=')[0].strip()]
        # "MouseButtonEventArgs e" -> 取倒数第二个词才是类型
        actual = params[1].split()[-2].rstrip('?') if len(params) >= 2 and len(params[1].split()) >= 2 else '(缺参数)'
        checked += 1
        if expected != '<未知控件>' and actual.split('.')[-1] != expected.split('.')[-1]:
            problems.append((xaml, handler, tag, expected, actual))

print('检查 Click 绑定', checked, '处')
print('问题', len(problems), '处')
for p in problems:
    print('  ', os.path.relpath(p[0], ROOT), '|', p[2], '|', p[1], '| 期望', p[3], '| 实际', p[4])
