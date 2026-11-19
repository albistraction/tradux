// Escolha de um conjunto de XSDs (origem ou destino): arquivos, arquivo
// principal e elemento raiz. Só h()/textContent.

import { h, select } from './dom.js';
import { mergeFiles, removeFile } from '../model/files.js';

/** Estado da escolha, guardado pelo editor entre redesenhos (o <input type="file"> é recriado a cada um). */
export const emptyPicker = () => ({ files: [], entryPoint: null, rootDraft: '', open: false, error: null });

/** Arquivo principal efetivo: o escolhido, se ainda estiver na lista; senão, o primeiro. */
export const entryPointOf = (picker) =>
  (picker.files.some((f) => f.name === picker.entryPoint) ? picker.entryPoint : picker.files[0]?.name ?? null);

/**
 * props: {
 *   idPrefix ('' destino, 'source-' origem — ids estáveis para leitores de tela e o gate e2e),
 *   picker (estado acima), loaded ({ entryPoint, rootElementName }|null), labels: { summaryNew, summaryLoaded, files, load },
 *   onChange(patch) (atualiza o estado da escolha e redesenha), onRootDraft(value) (sem redesenhar), onToggle(open), onLoad()
 * }
 */
export function schemaPicker({ idPrefix, picker, loaded, labels, onChange, onRootDraft, onToggle, onLoad }) {
  const filesId = `${idPrefix}xsd-files`;
  const names = picker.files.map((f) => f.name);
  return [
    loaded && h('p', { class: 'muted', id: `${idPrefix}schema-status` }, `Carregado: ${loaded.entryPoint} → <${loaded.rootElementName}>`),
    h('details', { open: !loaded || picker.open, ontoggle: (e) => onToggle(e.target.open) },
      h('summary', {}, loaded ? labels.summaryLoaded : labels.summaryNew),
      // Escolha que acumula: cada seleção soma à lista (o principal e, depois, os que ele inclui/importa).
      h('label', { for: filesId }, labels.files),
      h('input', {
        id: filesId, type: 'file', accept: '.xsd', multiple: true,
        onchange: (e) => onChange({ files: mergeFiles(picker.files, [...e.target.files]) }),
      }),
      h('p', { class: 'hint' }, 'Adicione o XSD principal e todos os que ele inclui/importa (ex.: xmldsig-core-schema.xsd) — pode escolher em mais de uma vez.'),
      picker.files.length > 0 && h('ul', { class: 'file-list', 'aria-live': 'polite', 'aria-label': 'Arquivos selecionados' },
        picker.files.map((f) => h('li', {},
          h('span', {}, f.name), ' ',
          h('button', {
            type: 'button', class: 'link', 'aria-label': `Remover ${f.name}`,
            onclick: () => onChange({ files: removeFile(picker.files, f.name) }),
          }, 'remover')))),
      names.length > 1 && h('label', {}, 'Arquivo principal ',
        select({ id: `${idPrefix}entry-point`, onchange: (e) => onChange({ entryPoint: e.target.value }) },
          names.map((n) => ({ value: n, label: n })), entryPointOf(picker))),
      h('label', { for: `${idPrefix}root-element` }, 'Elemento raiz (opcional)'),
      h('input', {
        id: `${idPrefix}root-element`, type: 'text', placeholder: 'detectado automaticamente', value: picker.rootDraft,
        oninput: (e) => onRootDraft(e.target.value),
      }),
      h('button', { type: 'button', class: 'primary', id: `load-${idPrefix}schema`, onclick: onLoad }, labels.load)),
    picker.error && h('p', { class: 'banner error', role: 'alert' }, picker.error),
  ];
}
