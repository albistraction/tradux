// Criação de elementos sem innerHTML: todo texto vindo de dados (nomes de
// campos, valores, mensagens do servidor) entra como textContent — sem
// risco de injeção de HTML/script.

/**
 * h('button', { class: 'x', onclick: fn, 'aria-label': '...' }, 'texto', filho...)
 * Atributos com valor null/undefined/false são ignorados; true vira atributo vazio.
 */
export function h(tag, attrs = {}, ...children) {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key.startsWith('on') && typeof value === 'function') {
      element.addEventListener(key.slice(2), value);
    } else if (key === 'value') {
      element.value = value;
    } else if (key === 'checked' || key === 'selected' || key === 'disabled') {
      element[key] = Boolean(value);
    } else {
      element.setAttribute(key, value === true ? '' : String(value));
    }
  }

  append(element, children);
  return element;
}

function append(element, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    element.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
}

export function replaceChildren(container, ...children) {
  container.replaceChildren();
  append(container, children);
}

/** <select> com opções [{ value, label, group? }] (agrupadas em <optgroup> quando há group). */
export function select(attrs, options, selectedValue) {
  const element = h('select', attrs);
  const groups = new Map();
  for (const option of options) {
    const node = h('option', { value: option.value, selected: option.value === selectedValue }, option.label);
    if (option.group) {
      if (!groups.has(option.group)) {
        const optgroup = h('optgroup', { label: option.group });
        groups.set(option.group, optgroup);
        element.append(optgroup);
      }

      groups.get(option.group).append(node);
    } else {
      element.append(node);
    }
  }

  element.value = selectedValue ?? '';
  return element;
}

/** Data e hora ISO da API no formato brasileiro (vazio se ausente). */
export const dateTime = (iso) => (iso ? new Date(iso).toLocaleString('pt-BR') : '');

let uid = 0;
/** Id único para ligar <label for> a controles. */
export const nextId = (prefix = 'c') => `${prefix}-${++uid}`;
