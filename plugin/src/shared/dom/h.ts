/**
 * Petit helper qui crée des éléments DOM. Il n'utilise pas innerHTML. Les données
 * des sites d'annonces ne peuvent donc pas injecter du HTML.
 */
type Child = Node | string | number | null | undefined | false;

export interface HProps {
  style?: Partial<CSSStyleDeclaration>;
  class?: string;
  title?: string;
  on?: Partial<{ [K in keyof HTMLElementEventMap]: (e: HTMLElementEventMap[K]) => void }>;
  attrs?: Record<string, string>;
  [prop: string]: unknown;
}

export function h<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  props: HProps | null = null,
  ...children: (Child | Child[])[]
): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  if (props) {
    const { style, class: cls, on, attrs, ...rest } = props;
    if (style) Object.assign(el.style, style);
    if (cls) el.className = cls;
    if (on) for (const [ev, fn] of Object.entries(on)) el.addEventListener(ev, fn as EventListener);
    if (attrs) for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
    Object.assign(el, rest);
  }
  for (const c of children.flat()) {
    if (c === null || c === undefined || c === false) continue;
    el.append(typeof c === 'number' ? String(c) : c);
  }
  return el;
}
