// MappingTemplate — o JSON do motor que POST /v1/design/test recebe e que cada
// versão publicada guarda — nos dois sentidos, com os nomes de campo do JSON
// só aqui. Módulo puro.
//   buildMappingTemplate(árvore, mapeamentos) → MappingTemplate
//   mappingsFromTemplate(árvore, MappingTemplate) → { mappings, problems }
// Os mapeamentos são o mapa por caminho de destino descrito em draft.js; a
// árvore é a de POST /v1/design/schema.
//
// RuleNode: { $type: 'field', path, scope? } | { $type: 'literal', value }
//         | { $type: 'operator', operatorName, arguments: RuleNode[] }

import { operator } from './ruleNodes.js';
import { toValueSpec, fromValueSpec } from './conditions.js';
import { OPERATOR } from './operators.js';
import { isRepeatable, isChoiceContent, subtreeHasMapping } from './draft.js';

// ---------------------------------------------------------------- RuleNode ⇄ ValueSpec

export function toRuleNode(spec) {
  switch (spec?.kind) {
    case 'field':
      return spec.scope === 'root' ? { $type: 'field', path: spec.path, scope: 'root' } : { $type: 'field', path: spec.path };
    case 'literal':
      return { $type: 'literal', value: spec.value ?? null };
    case 'operator':
      return { $type: 'operator', operatorName: spec.name, arguments: (spec.args ?? []).map(toRuleNode) };
    default:
      throw new Error('Expressão incompleta.');
  }
}

export function fromRuleNode(node) {
  switch (node?.$type) {
    case 'field':
      return node.scope === 'root' ? { kind: 'field', path: node.path, scope: 'root' } : { kind: 'field', path: node.path };
    case 'literal':
      return { kind: 'literal', value: node.value ?? null };
    case 'operator':
      return { kind: 'operator', name: node.operatorName, args: (node.arguments ?? []).map(fromRuleNode) };
    default:
      throw new Error(`Regra com tipo desconhecido (${node?.$type ?? 'vazio'}).`);
  }
}

/** RuleNode da condição do construtor, ou null (sem condição). */
export function toConditionRuleNode(group) {
  const spec = toValueSpec(group);
  return spec ? toRuleNode(spec) : null;
}

export const conditionFromRuleNode = (node) => fromValueSpec(fromRuleNode(node));

// ---------------------------------------------------------------- escalar: valor + padrão + condição

/** O valor padrão vai ao motor como setDefault(valor, padrão). */
function scalarToRuleNode(entry) {
  if (!entry?.value) return null;
  return toRuleNode(entry.defaultValue ? operator(OPERATOR.setDefault, [entry.value, entry.defaultValue]) : entry.value);
}

/** setDefault(valor, constante) volta a ser "valor + padrão", como o editor monta. */
function scalarFromTemplate(mapping) {
  const value = fromRuleNode(mapping.value);
  const entry = value.kind === 'operator' && value.name === OPERATOR.setDefault && value.args.length === 2 && value.args[1].kind === 'literal'
    ? { value: value.args[0], defaultValue: value.args[1] }
    : { value };
  if (mapping.condition) entry.condition = conditionFromRuleNode(mapping.condition);
  return entry;
}

// ---------------------------------------------------------------- ida

const emptyContent = () => ({ fields: [], complex: [], collections: [] });
const hasAnyMapping = (content) => content.fields.length + content.complex.length + content.collections.length > 0;

export function buildMappingTemplate(root, mappings) {
  return buildContent(root, mappings);
}

function buildContent(node, mappings, active = true, content = emptyContent()) {
  // Conteúdo xs:choice direto (inclusive na raiz): os filhos são ramos opcionais.
  if (isChoiceContent(node)) active = false;
  for (const child of node.children ?? []) {
    if (!child.mappable) continue;
    if (child.kind === 'Group') {
      // Grupo (sequence/choice/all) é transparente: os filhos entram no conteúdo do elemento (ADR-0018).
      buildContent(child, mappings, active && child.contentModel !== 'choice' && child.minOccurs >= 1, content);
      continue;
    }

    if (child.kind === 'Scalar') {
      const entry = mappings[child.path];
      const value = scalarToRuleNode(entry);
      if (!value) continue;
      const mapping = { destinationElementName: child.name, value };
      const condition = toConditionRuleNode(entry.condition);
      if (condition) mapping.condition = condition;
      content.fields.push(mapping);
    } else if (isRepeatable(child)) {
      const forEach = mappings[child.path]?.forEach;
      if (!forEach?.collectionPath) continue;
      const collection = {
        destinationElementName: child.name,
        sourceCollectionPath: forEach.collectionPath,
        itemContent: buildContent(child, mappings, !isChoiceContent(child), emptyContent()),
      };
      if (forEach.singleObject) collection.singleObjectAsItem = true;
      const filter = toConditionRuleNode(forEach.filter);
      if (filter) collection.filter = filter;
      if (forEach.orderBy) collection.orderByKey = toRuleNode(forEach.orderBy);
      content.collections.push(collection);
    } else {
      // Complexo obrigatório sob pai ativo é sempre emitido — mesmo sem
      // nenhum filho mapeado (ex.: envelope <itens/> com item opcional), igual
      // ao que computeStatus mostra e ao que o motor exige (revisão da Fase 6, B1).
      const required = active && child.minOccurs >= 1;
      const inner = buildContent(child, mappings, (required || subtreeHasMapping(child, mappings)) && !isChoiceContent(child), emptyContent());
      if (required || hasAnyMapping(inner)) content.complex.push({ destinationElementName: child.name, content: inner });
    }
  }

  return content;
}

// ---------------------------------------------------------------- volta

/** Filhos de conteúdo de um elemento: grupos (sequence/choice/all) são transparentes. */
function contentChildren(node) {
  return (node.children ?? []).flatMap((child) => (child.kind === 'Group' ? contentChildren(child) : [child]));
}

/**
 * Nada some em silêncio: o que não encontra destino na árvore, ou não pode
 * ser representado no editor, vai para `problems`.
 */
export function mappingsFromTemplate(root, template) {
  const mappings = {};
  const problems = [];

  const visit = (node, content) => {
    const children = contentChildren(node);
    const byName = (name, accept) => children.find((c) => c.name === name && c.mappable !== false && accept(c));

    for (const mapping of content?.fields ?? []) {
      const target = byName(mapping.destinationElementName, (c) => c.kind === 'Scalar');
      if (!target) {
        problems.push(`${node.path}/${mapping.destinationElementName}: campo sem destino na árvore.`);
        continue;
      }

      try {
        mappings[target.path] = scalarFromTemplate(mapping);
      } catch (e) {
        problems.push(`${target.path}: ${e.message}`);
      }
    }

    for (const complex of content?.complex ?? []) {
      const target = byName(complex.destinationElementName, (c) => c.kind === 'Complex' && !isRepeatable(c));
      if (!target) {
        problems.push(`${node.path}/${complex.destinationElementName}: bloco sem destino na árvore.`);
        continue;
      }

      visit(target, complex.content);
    }

    for (const collection of content?.collections ?? []) {
      const target = byName(collection.destinationElementName, (c) => c.kind === 'Complex' && isRepeatable(c));
      if (!target) {
        problems.push(`${node.path}/${collection.destinationElementName}: bloco "for each" sem destino na árvore.`);
        continue;
      }

      const forEach = { collectionPath: collection.sourceCollectionPath };
      if (collection.singleObjectAsItem) forEach.singleObject = true;
      try {
        if (collection.filter) forEach.filter = conditionFromRuleNode(collection.filter);
        if (collection.orderByKey) forEach.orderBy = fromRuleNode(collection.orderByKey);
      } catch (e) {
        problems.push(`${target.path}: ${e.message}`);
      }

      mappings[target.path] = { forEach };
      visit(target, collection.itemContent);
    }
  };

  visit(root, template);
  return { mappings, problems };
}
