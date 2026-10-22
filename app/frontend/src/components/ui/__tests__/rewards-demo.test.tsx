import { act, fireEvent, render, screen } from "@testing-library/react";
import RewardsDemo, { type RewardsDemoSnapshot } from "../rewards-demo";

const snapshot: RewardsDemoSnapshot = {
    checkedIn: false, balance: 0, rewardItem: "Crunchy Taco", rewardCost: 250,
    pointsPerDollar: 10, demoCodes: ["DEMO-PLUS", "DEMO-NEW"], redeemed: false,
    completed: false, canRedeem: false, pointsToEarn: 0, pointsAwarded: 0, discount: 0,
    message: "Use a fictional demo code. Never enter real member information."
};
const active = { ...snapshot, checkedIn: true, balance: 500 };
const buttons = () => screen.getAllByRole("button").map(button => button.textContent);

describe("simulated rewards lifecycle", () => {
    it.each([
        ["unchecked", snapshot, ["Check in"]],
        ["unavailable", active, ["Complete order"]],
        ["eligible", { ...active, canRedeem: true }, ["Use reward", "Complete order"]],
        ["reserved", { ...active, redeemed: true }, ["Undo reward", "Complete order"]],
        ["completed", { ...active, completed: true, redeemed: true }, ["Next order"]],
    ])("shows only the actions for %s", (_, state, expected) => {
        render(<RewardsDemo snapshot={state} connected onAction={vi.fn()} />);
        expect(buttons()).toEqual(expected);
        const primary = screen.getAllByRole("button").filter(button => button.classList.contains("bg-white"));
        expect(primary).toHaveLength(1);
    });

    it.each([snapshot, active, { ...active, redeemed: true, completed: true, balance: 369 }])(
        "offline overrides stale ticket state with only recovery", state => {
            const onReconnect = vi.fn();
            render(<RewardsDemo snapshot={state} connected={false} onAction={vi.fn()} onReconnect={onReconnect} />);
            expect(buttons()).toEqual(["Reconnect"]);
            expect(screen.queryByText(/demo points$/)).not.toBeInTheDocument();
            expect(screen.queryByLabelText("Demo rewards / check-in code")).not.toBeInTheDocument();
            expect(screen.queryByText("Order complete")).not.toBeInTheDocument();
            expect(screen.getByText(/fresh ticket and balance/)).toBeInTheDocument();
            fireEvent.click(screen.getByRole("button", { name: "Reconnect" }));
            expect(onReconnect).toHaveBeenCalledOnce();
            expect(screen.getByRole("button")).toBeDisabled();
            expect(screen.getByRole("status")).toHaveTextContent("Reconnecting");
        }
    );

    it("rejects arbitrary real-looking input without transmitting it", () => {
        const onAction = vi.fn();
        render(<RewardsDemo snapshot={snapshot} connected onAction={onAction} />);
        fireEvent.change(screen.getByLabelText("Demo rewards / check-in code"), { target: { value: "5551234567" } });
        fireEvent.click(screen.getByRole("button", { name: "Check in" }));
        expect(onAction).not.toHaveBeenCalled();
        expect(screen.getByRole("alert")).toHaveTextContent("No value was sent");
    });

    it("normalizes fictional codes, clears input and suppresses duplicate submissions", () => {
        const onAction = vi.fn();
        render(<RewardsDemo snapshot={snapshot} connected onAction={onAction} />);
        fireEvent.change(screen.getByLabelText("Demo rewards / check-in code"), { target: { value: "demo-plus" } });
        fireEvent.click(screen.getByRole("button", { name: "Check in" }));
        fireEvent.click(screen.getByRole("button", { name: "Check in" }));
        expect(onAction).toHaveBeenCalledExactlyOnceWith("check_in", "DEMO-PLUS");
        expect(screen.getByLabelText("Demo rewards / check-in code")).toHaveValue("");
        expect(screen.getByRole("status")).toHaveTextContent("Checking in");
    });

    it("offers the server-selected sweet and exposes subordinate undo after acknowledgement", () => {
        const onAction = vi.fn();
        const eligible = { ...active, canRedeem: true, rewardItem: "Cinnamon Twists", pointsToEarn: 119 };
        const { rerender } = render(<RewardsDemo snapshot={eligible} connected onAction={onAction} />);
        expect(screen.getByText(/Cinnamon Twists · 250/)).toBeInTheDocument();
        expect(screen.getByText(/Earn 119/)).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Use reward" }));
        expect(onAction).toHaveBeenCalledWith("redeem");
        expect(screen.getByRole("button", { name: "Complete order" })).toBeDisabled();
        rerender(<RewardsDemo snapshot={{ ...eligible, redeemed: true, canRedeem: false }} connected onAction={onAction} />);
        fireEvent.click(screen.getByRole("button", { name: "Undo reward" }));
        expect(onAction).toHaveBeenLastCalledWith("unredeem");
    });

    it("completed state shows earned points and balance with only Next order", () => {
        const onAction = vi.fn();
        render(<RewardsDemo snapshot={{ ...active, completed: true, pointsAwarded: 119, balance: 369 }} connected onAction={onAction} />);
        expect(screen.getByText(/Earned 119 fictional points/)).toBeInTheDocument();
        expect(screen.getByText(/369/)).toBeInTheDocument();
        expect(screen.queryByText(/Crunchy Taco/)).not.toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Next order" }));
        expect(onAction).toHaveBeenCalledWith("reset");
    });

    it("shows server rejection without optimistic state or a stuck pending control", () => {
        const onAction = vi.fn();
        const { rerender } = render(<RewardsDemo snapshot={active} connected onAction={onAction} />);
        fireEvent.click(screen.getByRole("button", { name: "Complete order" }));
        rerender(<RewardsDemo snapshot={{ ...active, message: "Add an item before completing this demo order." }} connected onAction={onAction} />);
        expect(screen.getByRole("status")).toHaveTextContent("Add an item");
        expect(screen.getByRole("button", { name: "Complete order" })).toBeEnabled();
        expect(screen.queryByRole("button", { name: "Next order" })).not.toBeInTheDocument();
    });

    it("reports thrown send/reconnect failures and allows retry", async () => {
        render(<RewardsDemo connected={false} onAction={vi.fn()} onReconnect={() => Promise.reject(new Error("offline"))} />);
        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Reconnect" })));
        expect(screen.getByRole("alert")).toHaveTextContent("Could not send");
        expect(screen.getByRole("button", { name: "Reconnect" })).toBeEnabled();
    });

    it("times out missing acknowledgement without assuming success or retrying automatically", () => {
        vi.useFakeTimers();
        try {
            const onAction = vi.fn();
            render(<RewardsDemo snapshot={active} connected onAction={onAction} />);
            fireEvent.click(screen.getByRole("button", { name: "Complete order" }));
            act(() => vi.advanceTimersByTime(10000));
            expect(screen.getByRole("alert")).toHaveTextContent("No confirmation received");
            expect(onAction).toHaveBeenCalledOnce();
            expect(screen.getByRole("button", { name: "Complete order" })).toBeEnabled();
        } finally { vi.useRealTimers(); }
    });

    it("loads missing state and clears pending/input on disconnect", () => {
        const onAction = vi.fn();
        const { rerender } = render(<RewardsDemo connected onAction={onAction} />);
        fireEvent.click(screen.getByRole("button", { name: "Load rewards" }));
        expect(onAction).toHaveBeenCalledWith("status");
        rerender(<RewardsDemo snapshot={snapshot} connected onAction={onAction} />);
        fireEvent.change(screen.getByLabelText("Demo rewards / check-in code"), { target: { value: "DEMO-PLUS" } });
        rerender(<RewardsDemo snapshot={snapshot} connected={false} onAction={onAction} onReconnect={vi.fn()} />);
        rerender(<RewardsDemo snapshot={snapshot} connected onAction={onAction} />);
        expect(screen.getByLabelText("Demo rewards / check-in code")).toHaveValue("");
    });
});
