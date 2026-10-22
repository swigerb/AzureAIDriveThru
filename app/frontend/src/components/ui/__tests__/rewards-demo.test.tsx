import { fireEvent, render, screen } from "@testing-library/react";
import RewardsDemo, { type RewardsDemoSnapshot } from "../rewards-demo";

const snapshot: RewardsDemoSnapshot = {
    checkedIn: false, balance: 0, rewardItem: "Crunchy Taco", rewardCost: 250,
    pointsPerDollar: 10, demoCodes: ["DEMO-PLUS", "DEMO-NEW"], redeemed: false,
    completed: false, canRedeem: false, pointsToEarn: 0, pointsAwarded: 0, discount: 0,
    message: "Use a fictional demo code. Never enter real member information."
};

describe("simulated rewards", () => {
    it("rejects arbitrary real-looking values locally without transmitting them", () => {
        const onAction = vi.fn();
        render(<RewardsDemo snapshot={snapshot} connected onAction={onAction} />);
        fireEvent.change(screen.getByLabelText("Demo rewards / check-in code"), { target: { value: "5551234567" } });
        fireEvent.click(screen.getByRole("button", { name: "Check in to demo order" }));
        expect(onAction).not.toHaveBeenCalled();
        expect(screen.getByRole("alert")).toHaveTextContent("No value was sent");
    });

    it("normalizes only known fictional codes and clears the transient field", () => {
        const onAction = vi.fn();
        render(<RewardsDemo snapshot={snapshot} connected onAction={onAction} />);
        fireEvent.change(screen.getByLabelText("Demo rewards / check-in code"), { target: { value: "demo-plus" } });
        fireEvent.click(screen.getByRole("button", { name: "Check in to demo order" }));
        expect(onAction).toHaveBeenCalledWith("check_in", "DEMO-PLUS");
        expect(screen.getByLabelText("Demo rewards / check-in code")).toHaveValue("");
        expect(screen.getByText(/No real account, payment or purchase/)).toBeInTheDocument();
    });

    it("proactively offers only server-eligible rewards without computing points client-side", () => {
        const onAction = vi.fn();
        render(<RewardsDemo snapshot={{ ...snapshot, checkedIn: true, balance: 500, canRedeem: true, pointsToEarn: 79 }} connected onAction={onAction} />);
        expect(screen.getByText(/You have an eligible reward/)).toBeInTheDocument();
        expect(screen.getByText(/Earn 79 points on completion/)).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Redeem demo reward" }));
        expect(onAction).toHaveBeenCalledWith("redeem");
    });

    it("does not enable spending when the server says the reward is unavailable", () => {
        render(<RewardsDemo snapshot={{ ...snapshot, checkedIn: true, balance: 500 }} connected onAction={vi.fn()} />);
        expect(screen.getByRole("button", { name: "Redeem demo reward" })).toBeDisabled();
        expect(screen.getByText(/Add a plain Crunchy Taco/)).toBeInTheDocument();
    });

    it("allows cancellation of a reserved reward and never repeats completed awards", () => {
        const onAction = vi.fn();
        const { rerender } = render(<RewardsDemo snapshot={{ ...snapshot, checkedIn: true, redeemed: true }} connected onAction={onAction} />);
        fireEvent.click(screen.getByRole("button", { name: "Remove demo reward" }));
        expect(onAction).toHaveBeenCalledWith("unredeem");
        rerender(<RewardsDemo snapshot={{ ...snapshot, checkedIn: true, completed: true, pointsAwarded: 19 }} connected onAction={onAction} />);
        expect(screen.getByRole("button", { name: "Complete demo order" })).toBeDisabled();
        expect(screen.getByText(/19 fictional points earned/)).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Start next demo order" }));
        expect(onAction).toHaveBeenCalledWith("reset");
    });

    it("prevents mutations while disconnected and exposes recovery before state arrives", () => {
        const onAction = vi.fn();
        const onReconnect = vi.fn();
        const { rerender } = render(<RewardsDemo snapshot={snapshot} connected={false} onAction={onAction} onReconnect={onReconnect} />);
        expect(screen.getByLabelText("Demo rewards / check-in code")).toBeDisabled();
        expect(screen.getByText(/Rewards are offline/)).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Check in to demo order" })).toBeDisabled();
        fireEvent.click(screen.getByRole("button", { name: "Reconnect demo rewards" }));
        expect(onReconnect).toHaveBeenCalledOnce();
        expect(onAction).not.toHaveBeenCalled();
        rerender(<RewardsDemo connected onAction={onAction} />);
        fireEvent.click(screen.getByRole("button", { name: "Load demo rewards" }));
        expect(onAction).toHaveBeenCalledWith("status");
    });

    it("offers the server-selected late-added sweet reward without inventing eligibility", () => {
        render(<RewardsDemo snapshot={{ ...snapshot, rewardItem: "Cinnamon Twists", checkedIn: true, canRedeem: true }} connected onAction={vi.fn()} />);
        expect(screen.getByText(/Cinnamon Twists · 250 demo points/)).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Redeem demo reward" })).toBeEnabled();
    });
});
