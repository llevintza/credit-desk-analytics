// @ts-check
/**
 * README §9.4 / web/AGENTS.md: every manual `.subscribe(` needs `takeUntilDestroyed` (#209).
 *
 * Passes only when the receiver is a `.pipe(...)` call whose last operator is `takeUntilDestroyed(...)`.
 * Last, because an operator after it (e.g. `switchMap`) can still hold an inner subscription open after destroy.
 * Prefer `toSignal` or the async pipe; the one documented exception (HealthService, ADR-0010) is an inline disable.
 *
 * @type {import('eslint').Rule.RuleModule}
 */
module.exports = {
  meta: {
    type: 'problem',
    docs: { description: 'Require takeUntilDestroyed as the last operator before a manual subscribe' },
    schema: [],
    messages: {
      unmanaged:
        'Manual subscribe without cleanup: pipe takeUntilDestroyed(...) as the last operator, or use toSignal / the async pipe (README §9.4).',
      notLast:
        'takeUntilDestroyed(...) must be the last operator in the pipe before subscribe; operators after it can outlive destroy (README §9.4).',
    },
  },
  create(context) {
    return {
      CallExpression(node) {
        const callee = node.callee;
        if (callee.type !== 'MemberExpression' || propertyName(callee) !== 'subscribe') return;
        const receiver = callee.object;
        const ops =
          receiver.type === 'CallExpression' &&
          receiver.callee.type === 'MemberExpression' &&
          propertyName(receiver.callee) === 'pipe'
            ? receiver.arguments
            : [];
        const index = ops.findIndex(isTakeUntilDestroyed);
        if (index === -1) {
          context.report({ node: callee.property, messageId: 'unmanaged' });
        } else if (index !== ops.length - 1) {
          context.report({ node: callee.property, messageId: 'notLast' });
        }
      },
    };
  },
};

/** @param {import('estree').MemberExpression} member */
function propertyName(member) {
  const p = member.property;
  if (!member.computed && p.type === 'Identifier') return p.name;
  if (member.computed && p.type === 'Literal') return p.value;
  return undefined;
}

/** @param {import('estree').Node} arg */
function isTakeUntilDestroyed(arg) {
  return arg.type === 'CallExpression' && arg.callee.type === 'Identifier' && arg.callee.name === 'takeUntilDestroyed';
}
